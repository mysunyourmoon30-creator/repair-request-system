RR-RCA-CI-001

# CI reliability — fixture database drop failure (SQL error 3702) — RCA note

Test-infrastructure note only. It changes no business rule, no baseline document and no production code. It records what is **proven**, what is only a **hypothesis**, what the added diagnostics can and cannot show, and what the next occurrence should decide. **The original failure is not fixed and no fix is claimed.**

## 1. Document control

| Field | Value |
|---|---|
| Document ID | RR-RCA-CI-001 |
| Version | 1.1 (14–17 October 2026): 1.0 recorded the first incident and the diagnostics; 1.1 adds the first recurrence (section 8) |
| Status | OPEN — cause not established; bounded diagnostics merged (PR #29); first recurrence captured and only partially informative |
| Subject run | GitHub Actions run `38586343631`, **attempt 1**, Backend (.NET), on `c1b1b1980d3009d687443d135a78277214828a37` (`main`, merge of PR #28); attempt 2 (same commit) passed |
| Evidence | the attempt-1 `backend-test-results` artifact (`11905233681`, created 15:16:05 UTC, inside attempt 1); the attempt-2 artifact (`11906951651`) was not used |
| Related | PR #26 (`94aff48`, LocalDB lifecycle gate); `tests/RepairRequest.ApiTests/LocalDbLifecycleGate.cs`, `ApiHostFactory.cs`, `FixtureDatabaseDropDiagnostics.cs` |
| Earlier note | none: PR #26's root-cause text exists only in its commit message; this is the first RCA note |

## 2. Proven facts (from the attempt-1 TRX and log)

1. **All tests passed, the step still failed.** Domain 557, Application 682, Integration 462 (2 skipped), API 722 — 0 failures. The Test step exited 1 because of one `Test Class Cleanup Failure (RepairRequest.ApiTests.DevelopmentScanning.DevelopmentMalwareScanningTestingTests)`, which xUnit reports as a run error.
2. **Failed operation.** `DROP DATABASE [RepairRequestDb_DevScanTestingApiTest]`, issued by EF Core `EnsureDeletedAsync` from `AuthApiFactory.DisposeAsync` (`ApiHostFactory.cs:64`), inside the PR #26 lifecycle gate. Error: `SqlException: Cannot drop database "…" because it is currently in use.` (error 3702). The command failed after **20,004 ms** (command timeout was 60 s, so this is not a client timeout; it matches SQL Server's wait for exclusive access before it gives up).
3. **Ownership.** The database name belongs to exactly one fixture (`TestingFakeScanningFactory`, used only by `DevelopmentMalwareScanningTestingTests`).
4. **Timeline of the fixture (UTC, same day).** Host built 15:10:04; runner logged "Cannot open database" until about 15:10:16 (the database did not exist yet — expected); tests ran 15:10:16.5–15:10:17.83; the drop started ≈ 15:10:18.3 (0.5 s after the last test) and failed at 15:10:38.33. The drop was therefore **not** delayed by the gate.
5. **Cleanup ordering.** `DisposeAsync` drops the database first and disposes the factory's host afterwards. At drop time the host — including `DevelopmentOnlyPendingScanRunner`, a `BackgroundService` that polls the database every 200 ms (`PollInterval`) in this fixture — was still running. The runner logged nothing for this database during the 20 s wait.
6. **Consequence for other fixtures.** The failed drop held the lifecycle gate for the full 20 s; fixtures that started in that window (hosts built before they wait for the gate) logged login failures for their not-yet-created databases (`CorrectiveActionApiTest`, `CorsPolicyApiTest`). These are effects, not causes.
7. **Frequency.** The signature appears in 1 of ≈ 41 CI run-attempts examined (the latest attempt of the 40 most recent runs plus this attempt 1).

## 3. Relation to PR #26

PR #26 fixed concurrent `CREATE`/`MIGRATE`/`DROP` of several databases against one LocalDB instance ("Could not obtain exclusive lock on database 'model'", create/commit timeouts) by serializing those steps with an application lock. This failure is a different mechanism: it is about **one fixture's own database** being in use by something at drop time. The gate does not cover the fixture's own live host and neither prevents nor causes it (it only made the 20 s wait visible to other fixtures). It is therefore **not** labeled "LocalDB contention", and the rerun passing is not treated as evidence of a cause.

## 4. Hypotheses (not proven)

| # | Hypothesis | For | Against / open |
|---|---|---|---|
| H1 (leading) | a connection of the fixture's own runner (in flight, or pooled again around the drop) held a session in the database | the runner is the only timer-driven user of that database; drops with a 200 ms poller alive took up to 4.0 s against 1.0 s with the poller stopped (25 cycles each, throwaway databases) | **not reproduced:** 0 failures in 40 local runs of the two runner-enabled fixtures (80 drops), 0/25 poller-alive and 0/25 poller-stopped diagnostic drops; why a continuous poller would hold the database for a full 20 s only occasionally is not explained |
| H2 | a session of another process or fixture on the shared instance | — | the name is unique to this fixture |
| H3 | an environment stall (slow runner disk, as in PR #26) stretched a normally short wait past 20 s | consistent with PR #26's findings | no direct evidence |

## 5. What was added (diagnostics only)

`FixtureDatabaseDropDiagnostics` wraps the two `EnsureDeletedAsync` calls of `AuthApiFactory` (start-up clean and dispose). Order, gate, parallelism and exception behavior are unchanged. Only when the call fails with **error 3702** (found in the exception chain) it:

- prints one identity line to `Console.Out` (kept by vstest in the TRX standard output, verified): fixture type, database name, error number/state/class, drop start and failure time (UTC), drop duration, message;
- opens a **separate, unpooled connection to `master`** (5 s connect timeout, application name `rr-fixture-drop-diagnostics`) and runs three **read-only** queries restricted to that database's id: sessions (`sys.dm_exec_sessions`: id, status, program name, user-process flag, open transactions, login/last-request times), requests (`sys.dm_exec_requests`: id, status, command, wait type and time, **blocking session id**, start) and locks (`sys.dm_tran_locks` grouped by session, resource type, mode, status, count); at most 50 rows each; session/request times are SQL Server **local** time;
- stops after a hard **10 s** total timeout;
- never lets a collector failure (permissions without `VIEW SERVER STATE`, connection errors, timeout) replace the original: a one-line note is printed instead and the **original exception is rethrown with `throw;`** (same object, original frames kept — covered by a test that fails if it is changed to `throw exception;`).

Not collected: SQL text, parameters, other databases (system database names are refused), login names. Other errors and success behave exactly as before.

## 6. Limits of the diagnostics

- The snapshot is taken **after** the 20 s wait ended in failure. A holder that disappeared before collection is not seen. **An empty snapshot is inconclusive**, not proof that nothing held the database; "database no longer exists" is likewise inconclusive.
- A sleeping pooled connection shows as a session with a `DATABASE` shared lock and no request; a running holder shows a request with its wait type and blocking id.
- Only `AuthApiFactory` (the API test factories) is instrumented. Other fixtures that drop databases (Integration tests, throwaway schema tests) are not.
- The collector runs inside the lifecycle gate, so on the failure path the gate is held up to 10 s longer than before.

## 7. How the next occurrence decides

| What the snapshot shows | Reading | Next step |
|---|---|---|
| a session with the test process's default SqlClient `program_name`, `status=running`/`sleeping`, and a request or `DATABASE` lock, while the runner is the only timer-driven user of the database | supports H1 | evaluate the reordering fix (stop the fixture's host and release its pool before the drop, using a standalone context) and verify it against this evidence |
| a holder with a long-running request or an open transaction | a leaked or stuck operation of a test | find the test by timestamps and fix that test |
| an application name that is not from the test process | H2 | identify the other process |
| an empty snapshot or "no longer exists" | the holder was gone | no conclusion; keep the diagnostics; consider collecting earlier only with an owner decision (it would change the cleanup path) |

No retry, suppression, forced disconnect, pool clearing or parallelism change is proposed by this note.

## 8. First recurrence with diagnostics — run 39009375793, attempt 1 (17 October 2026)

First post-merge run after PR #29 (`129897e91da71b4e1d504a4c98d1c67a467b1228`, Backend attempt 1; artifact `12092038202`, created 06:28:40 UTC, preserved before any rerun). The Frontend job passed; the Backend job failed with **two independent problems**.

### 8.1 The 3702 recurrence

| Item | Value |
|---|---|
| Fixture / class | `DevelopmentFakeScanningFactory` / `DevelopmentMalwareScanningDevelopmentTests` (the **other** fixture that hosts the 200 ms scan runner; the first incident was `TestingFakeScanningFactory`) |
| Database | `RepairRequestDb_DevScanDevelopmentApiTest` (database id 6) |
| Error | 3702, state 4, class 16, "currently in use" |
| Drop | started 06:20:15.033, failed 06:20:35.215 UTC — **20,182 ms** (same 20 s wait as before). The class's only test had ended at 06:20:08.1, about 7 s before the drop started; this evidence does not explain the interval (a wait for the lifecycle gate is plausible, not shown) |
| Snapshot | taken 06:20:35.254, ≈ 40 ms after the failure |
| Sessions (1) | session 88, `sleeping`, program name `EFCore/10.0.12 (Microsoft Windows …)`, user process, 0 open transactions, login time and last request both within 06:20:35 (server-local time equals UTC on the runner) |
| Requests (0) | none |
| Locks (1) | session 88 — `DATABASE`, mode `S`, status `GRANT`, count 1 |

**Reading — informative but not conclusive.**

- *Shown:* at collection time the fixture's database was in use by one sleeping connection whose program name is the default of **EF Core connections in a test process** — not by another program, not by an open transaction, not by a running request, and not blocked on anything.
- *Not shown:* that this session is what blocked the drop during the 20 s. It logged in **in the same second the drop failed**, so it may have been created after the wait ended (for example by the still-running host); whatever held the database earlier was gone by collection. The snapshot therefore does not name the holder of the 20 s wait.
- *Consistency, not proof:* both incidents are in the two fixtures that run the 200 ms polling runner; none occurred in the Production scanning fixture (no runner) or in the other API fixtures, over the ≈ 41 run-attempts examined plus this one. Two events are too few to treat the association as established.
- The log shows the host shutting down at 06:20:35, right after the failure.

Effect on the hypotheses of section 4: H1 (the fixture's own EF-based component, the runner) is supported a little more strongly; H2 (another process) is less likely; H3 is not distinguishable. Nothing is proven and **no fix is claimed**. Decision-table row 1 of section 7 now applies *partially*: the reordering fix (stop the fixture's host and release its pool before the drop) can be evaluated, but it needs an explicit decision and a way to verify it against a failure that is rare.

### 8.2 A failed test in the same attempt (unrelated to 3702)

One API test failed in attempt 1: `FixtureDatabaseDropDiagnosticsTests.Error3702_ReportsIdentityTimingError…`, assertion `Assert.Contains("OnError", thrown.StackTrace)`. Which SqlClient frames appear in a stack trace depends on JIT inlining; the same assertion passed in the PR's CI run. It is a defect of the test, not of the diagnostics or the drop: it is replaced by comparing the rethrown stack with the stack captured at the original catch (`throw;` keeps that text as its start, `throw exception;` resets it — verified by a mutation check, in Debug and Release).
