# Performance evidence — Work Order timeline (AUD-API-001) — Test Plan v1.0

Local evidence tooling for the profile approved in `docs/15` §7 (decision D11); results and environment are recorded in `docs/15` §13. **Nothing here runs in CI**, nothing here is an application code path or an EF migration, and nothing generated (fixture, summaries, logs, binaries) belongs in Git.

> The numbers produced with this tooling are **local evidence on a single developer laptop** (k6, the API host and SQL Server share one machine). They are **not a production capacity guarantee**.

## What is here
| Path | Purpose |
| --- | --- |
| `seed/RepairRequest.PerfSeed/` | .NET console tool (not in `RepairRequest.slnx`, never built by CI). `reset` recreates the dedicated LocalDB database `RepairRequestDb_PerfTimeline` from the real EF migrations, `seed` loads the profiles, `counts` prints row counts and per-profile expected vs SQL-oracle sizes, `verify` walks sample timelines through the running API and compares them with a SQL Server `ORDER BY` oracle (exactly once, newest first). |
| `k6/audit-timeline.js` | The k6 script (profiles `smoke`, `warmup`, `small`, `medium`, `large`, `manyids`, `bigids`, `negative`). |
| `sql/capture-snapshot.sql` | Read-only snapshot: row counts + id checksum, `@@DBTS`, timeline statements from the plan cache (executions, logical reads, worker/elapsed time, spills), plan-shape flags (scan of `audit_history`?), wait statistics, tempdb pages, sessions. |
| `scripts/capture-snapshot.ps1`, `scripts/sample-processes.ps1`, `scripts/run-profile.ps1` | Snapshot runner, CPU/memory sampler (API, `sqlservr`, `k6`), and one measured run (snapshot → run → snapshot). |

## Seed profiles (deterministic sizes; ids are regenerated on every `reset` + `seed`)
| Profile | Work Orders | Audit events per Work Order | Child entity ids per Work Order (Visits + Sessions + Corrective Actions) |
| --- | --- | --- | --- |
| SMALL | 100 | 20 | 5 |
| MEDIUM | 50 | 200 | 19 |
| LARGE | 20 | 2,000 | 90 |
| MANYIDS | 10 | 711 | 230 (232 entity ids with the Work Order and Repair Request) |
| BIGIDS | 5 | 2,421 | 2,400 real children (1,300 Visits, 1,000 Sessions, 100 Corrective Actions) |
| Background | 5,000 Work Orders + Repair Requests | — | plus 500,000 noise `audit_history` rows across three tenants |

Negative fixtures: a Work Order of another Site, a Work Order of another tenant, a missing id, a second Requester who does not own the Work Order, a Supervisor of another Site, a Technician and an Administrator.

## Running it (PowerShell)
```powershell
$env:PERF_FIXTURE_PATH = '<scratch>\fixture.json'          # outside the repository: it holds a generated test password
dotnet run -c Release --project tests/performance/seed/RepairRequest.PerfSeed -- reset
dotnet run -c Release --project tests/performance/seed/RepairRequest.PerfSeed -- seed
dotnet publish backend/src/RepairRequest.Api -c Release -o <scratch>\api-publish
# host the API (Production environment, plain http on loopback; connection string and a random Base64 JWT key from environment variables):
#   ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:5199
#   ConnectionStrings__DefaultConnection=...RepairRequestDb_PerfTimeline...  Authentication__Jwt__SigningKey=<random base64, 32 bytes>
dotnet run -c Release --project tests/performance/seed/RepairRequest.PerfSeed -- verify
.\tests\performance\scripts\run-profile.ps1 -K6 <path>\k6.exe -Profile large -Fixture <scratch>\fixture.json -OutDir <scratch>\runs -Label large-1
```
k6 is installed from the official release archive (`k6-vX.Y.Z-windows-amd64.zip`, SHA-256 checked against the published `k6-vX.Y.Z-checksums.txt`) into a scratch folder; no PATH or system change, no binary in Git.

## Test Plan v1.0 (adjusts the `docs/11` default profile of 5 / 30 / 5 minutes, as `docs/11` permits for a versioned plan)
| Run | Load | Notes |
| --- | --- | --- |
| Smoke | 1 VU, 35 s | contract checks and a full cursor walk of a 200-event timeline (exactly once, newest first); pageSize 0 / 100 / above 100; 401; 405 |
| Warm-up | 20 VUs, 60 s, all profiles | **discarded**, not counted |
| Acceptance (SMALL, MEDIUM, LARGE, BIGIDS; MANYIDS supplementary), separately | 30 s ramp to 100 VUs, **2 min 30 s hold**, 10 s ramp-down; think time **0.5 s** | 50 % first page, 50 % deep page (cursors at about 50 % and 80 % depth, precomputed in `setup()` by walking real pages), `pageSize=20`, callers round-robin over Supervisor, Coordinator, Team Lead, Approver and the owning Requester |
| Saturation (LARGE and BIGIDS), supplementary | same ramp, **no think time** | stress evidence only; it is not the gate |
| Negative | 10 VUs, 60 s, run on its own | 401 (none / invalid token), 403 (Technician, Administrator), 400 (malformed, random, tampered, foreign-bound cursor; pageSize 0), 404 (missing, other Site, other tenant, non-owner Requester, other-Site Supervisor, unsupported entity type) |

**Gate (per acceptance profile):** every response is validated (status, content type, `Cache-Control: no-store`, page shape, item fields and enums, UTC timestamps, non-increasing order, no forbidden property such as `reason`/`correlationId`/raw JSON/e-mail); the custom `unexpected_response` rate must be **0**; and **p95 ≤ 3.0 s** over the positive traffic. Reported per run: p50 / p90 / p95 / p99 / max, requests per second, failure rate, first page and deep page separately. A SMALL timeline has exactly one page (20 events), so it has no deep page.

Around every run: snapshot before and after (row counts, `@@DBTS`, id checksum, plan cache counters, waits, tempdb), CPU/memory sampling every 5 s, and an API-log scan for timeouts, pool exhaustion and exceptions. A logged-in setup appends `USER` / `AUTH_LOGIN_SUCCEEDED` audit rows by design; the timeline read path must append none.

## Related evidence
The Phase 1A spike and the production-store plan evidence are in `docs/15` §11–§12. The heavy plan/IO test is **opt-in**: `RUN_TIMELINE_EVIDENCE=1 dotnet test tests/RepairRequest.IntegrationTests --filter "FullyQualifiedName~AuditTimelineEvidenceTests"` (seeds 500,000 audit rows; `TIMELINE_EVIDENCE_PATH` chooses the output file, `TIMELINE_NOISE_ROWS` the volume). Default `dotnet test` skips it.

## Safety
The fixture contains a generated test password and user e-mail addresses: generate it locally, never commit it, and never point this tooling at a shared or production environment. The seed tool only touches the dedicated `RepairRequestDb_PerfTimeline` database.
