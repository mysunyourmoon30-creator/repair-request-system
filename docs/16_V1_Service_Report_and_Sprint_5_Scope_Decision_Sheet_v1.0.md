RR-DEC-V1-S5

# V1 Scope, Service Report and Sprint 5 Scope — Decision Sheet

Documentation only. This sheet records Portfolio Project Owner decisions **D1–D8** and the closed owner decisions **OD1–OD7** of 11 October 2026, the evidence behind them, the four-ticket Sprint 5 scope and the definitions of "V1 scope-complete" and "production-ready". It changes no code, test, migration or configuration, and no baseline PDF.

## 1. Document Control

| Field | Value |
|---|---|
| Document ID | RR-DEC-V1-S5 |
| Version | 1.0 |
| Status | Owner decisions D1–D8 and OD1–OD7 recorded (11 October 2026); documentation only; not yet committed. No owner decision remains open in this sheet (§10). |
| Revision Date | 11 October 2026 |
| Revision history | v1.0 — Sprint 5 Ticket 2 draft (D1–D8) and owner-review revision (OD1–OD7 closed; Ticket 4 renamed to include the Service Report; completion terminology split into "V1 scope-complete" and "production-ready"; concurrency wording generalised; `docs/15` status correction recorded), both 11 October 2026. |
| Baseline commit | `9eb05a7e5fbc41ffa569b931a18ca4b33dbbd91b` (`main`, merge of PR #22) |
| Extends / cross-references | RR-REQ-001 v1.6 (`01_...pdf`), BR-RR-BASELINE v1.6 (`02_...pdf`), RR-STS-001 v1.6 (`03_...pdf`), UC-RR-001 v1.7 (`04_...pdf`), RR-DD-001 v1.4 (`05_...pdf`), RR-TC-001 v1.3 (`06_...pdf`), RR-ERD-001 v1.2 (`07_...pdf`), RR-DBD-001 v1.2 (`08_...pdf`), RR-API-001 v1.2 (`09_...pdf`), RR-UI-001 v1.2 (`10_...pdf`), RR-PERF-001 v1.0 (`11_...pdf`), RR-ARCH-001 v1.1, RR-DEC-001 v1.2 (`12_...md`), RR-API-001-ADD v1.0 (`13_...md`), RR-DEC-S2-001 (`14_...md`), RR-DEC-UC-WO-002 v1.1 (`15_...md`) |
| Approval | Portfolio Project Owner decisions of 11 October 2026 (D1–D8, OD1–OD7) |
| Audience | Portfolio Project Owner, SA, Backend/Frontend Developer, Tester |
| Purpose | Define what "V1" and "Service Report" mean for this delivery, fix the Sprint 5 scope and its four tickets, and record the owner decisions the dependent tickets build on. |

**Revision rule (same as `docs/12`, `docs/14`, `docs/15`).** This document does not change any locked business semantics and modifies no baseline PDF (`docs/00`–`docs/11`, RR-ARCH-001). Statements the baseline makes are cited by document and page (or section where the page is not meaningful); page numbers are the printed "Page x / y" footers of the extracted PDF text, which can differ from the physical page by the leading revision page (the same caveat as `docs/15`). Statements fixed by the owner on top of the baseline are marked **OWNER DECISION**. Anything this sheet proposes but the owner has not approved is marked **OWNER-PROPOSED** (only the Sprint 5 size estimates in §6 carry that label). **"Sprint 5", "V1" and "Service Report" are owner delivery terms, not baseline terms:** the baseline PDFs contain no Sprint 1–5 definition (the only "Sprint" mentions are "Sprint 0 foundation" in `docs/00` p.2 and RR-ARCH-001 §23, and "Sprint planning after technical baseline approval" in `docs/10` p.14) and no "Service Report"; "V1" occurs only inside document version labels such as "v1.6" (confirmed by search of the extracted text of `docs/00`–`docs/11` and RR-ARCH-001; `docs/13` §4.21 Decision (e) records the same finding).

**Scope note.** Sprint 5 contains exactly **four** tickets (§6). Ticket 1 is DONE; this sheet is Ticket 2 (in review). Tickets 3 and 4 are not started, and no evidence or SLA implementation exists.

---

## 2. Baseline evidence and citations

| # | Finding | Source |
|---|---|---|
| F1 | No baseline document uses "Service Report" or "V1" as a delivery term. `docs/13` uses "Service Report" inconsistently: §4.22–§4.24 list it as a separate not-yet-built item, §4.25 equates it with `UC-REP-001`. | `docs/13` §4.21 Decision (e), §4.22–§4.25; re-search of `docs/00`–`docs/11`, RR-ARCH-001 |
| F2 | BR-06 (Check-out): *"Summary+Outcome + required CLEAN evidence; valid time sequence"*; the requirement text adds *"category-required CLEAN evidence; corrective resubmission always requires CLEAN evidence"*. | `docs/02` p.3 (BR-06); `docs/01` p.3 (BR-06 text) |
| F3 | ST-WO-003 (`IN_PROGRESS → AWAITING_SUPERVISOR_REVIEW`) guard: *"Session checked out; summary/outcome/evidence complete"*. This is a baseline guard on the **Work Summary submission**, in addition to the Check-out sources in F4. | `docs/03` p.3 (ST-WO-003) |
| F4 | Check-out sources that tie summary/outcome/evidence to Check-out: ST-SV-003 guard *"Summary/outcome/evidence + time sequence valid"*; UC-WO-016 *"Check-out validates summary/outcome/evidence"*; FR-06 first-release criterion *"Check-out requires complete summary/outcome/evidence"*. | `docs/03` p.3 (ST-SV-003); `docs/04` p.8 (UC-WO-016); `docs/01` p.5 (§10) |
| F5 | Approved deviation: Check-out is a pure state transition; the summary/outcome/evidence half is a separate Work Summary submission by the Technician. `docs/13` §4.14 lists four Check-out sources; ST-WO-003 (F3) is a fifth source that places the same guard on Work Summary submission. | `docs/13` §4.14, §4.15 Decisions 2/3/5 |
| F6 | Evidence data model: `EVD-001 evidence_id` (PK), `EVD-003 work_summary_id` (FK Summary), `EVD-004 evidence_type_code varchar(30)` *"Active Evidence Type"*, `EVD-013 file_asset_id` *"FK CLEAN FileAsset"*; `WSM-005 revision_no` unique per Visit revision, `WSM-006 summary_text`, `WSM-007 repair_outcome_code`. The dictionary lists only these EVD fields. | `docs/05` p.6 |
| F7 | ERD / DB design: entity `work_summary_evidence` (PK `evidence_id`; FK `work_summary_id`, `file_asset_id`; `evidence_type`); `FileAsset 1:N WorkSummaryEvidence`; *"evidence → file_asset"*, `UQ(service_visit_id, revision_no)`; the ERD reference list names **Evidence Type** as a reference master. | `docs/07` p.2 (entity diagram; reference-master list) and p.3 (relationship catalog); `docs/08` p.2 |
| F8 | File rules: `FAS-004 mime_type` *"PDF/JPG/JPEG/PNG"*, `FAS-005 size_bytes ≤ 10 MB`, `FAS-006 SHA-256`, `FAS-007` private reference, `FAS-008 malware_scan_status PENDING/CLEAN/FAILED`; NFR: *"private storage; SHA-256; malware scan; only CLEAN file can satisfy mandatory evidence"*; architecture: scan is asynchronous, only CLEAN files can satisfy mandatory evidence or be served. | `docs/05` p.4 (FAS-001..010); `docs/01` p.5 (§9 File NFR); RR-ARCH-001 §11 |
| F9 | `FileAsset` supports attachment, corrective plan and **work evidence** lifecycle (ERD-DR-05). PENDING/FAILED evidence must never become usable (*"cannot make PENDING/FAILED evidence usable"*); a FAILED file has *"No guarded transition"*. | `docs/07` p.3 (ERD-DR-05); `docs/02` performance appendix p.1 (§6 Files/Evidence); `docs/10` p.4 (file states) |
| F10 | Test intent: TC-WO-007 *"Check-out evidence — Category/corrective evidence rules — PENDING/FAILED then CLEAN — PENDING/FAILED cannot satisfy; CLEAN + summary/outcome required — no transition then COMPLETED"*; test masters include **Evidence Type**; test files include CLEAN, invalid type, >10 MB, PENDING, FAILED. | `docs/06` p.4 (TC-WO-007), p.2 (masters, files) |
| F11 | In the baseline, "category" in the evidence rules means the **Repair Request Category** (*"category decides conditional Equipment/Location and evidence policy"*). No evidence-policy field exists on Category; `docs/12` §8 records *"Category-specific evidence rules (category-required CLEAN evidence at Check-out)"* as DEFERRED — Work Order scope. (In OD1 the owner's evidence "categories" BEFORE_WORK / AFTER_WORK / OTHER are **evidence types** — `EVD-004`; the two meanings are kept apart throughout this sheet.) | `docs/01` p.6 (§12 master data); `docs/12` §8 |
| F12 | UI: UI-041 Work Session screen lists *evidence* among Technician actions; UI-050 Customer Acceptance shows *"Summary/evidence"*; UC-WO-021 main flow *"Review summary/evidence; Accept"*; UC-WO-020 preconditions *"complete summary/outcome/evidence"*; UC-WO-024 *"evidence required"*. | `docs/10` p.2 (UI-041, UI-050); `docs/04` p.9/10/11 |
| F13 | SLA: EV-SLA-001 *"started_at=submit; original_due=+24h; effective_due=original_due; risk_at=effective_due−4h"*; EV-SLA-005 *"Request REJECTED/CANCELLED or WO CLOSED/CANCELLED — Set stopped_at/reason; prior breach stays"*; `SLA-001..012` (one `sla_record` per Repair Request, `SLA-003` unique FK; `SLA-004 sla_policy_version` snapshot; `SLA-012 stop_reason` = REQUEST_REJECTED / REQUEST_CANCELLED / WO_CLOSED / WO_CANCELLED); BR-12 *"24h 24/7; sticky breach"*; UC-SLA-001/002; TC-SLA-001..003. | `docs/03` p.4 (§6 EV-SLA); `docs/05` p.8 (SLA-001..012, OVR); `docs/02` p.3 (BR-12/13/17); `docs/04` p.13 (UC-SLA-001/002); `docs/06` p.4 (TC-SLA-001..003) |
| F14 | SLA scope was deliberately deferred: Submit records only `submitted_at` as the SLA start marker (no `sla_record`, due, risk). The additional points in NB-9 and DEC-PRE-S1-010-02 are listed, **not adopted**, in §10.2. | `docs/12` §8, §9 NB-3/NB-9, DEC-PRE-S1-007-12 |
| F15 | The SLA stop is explicitly deferred in four shipped flows: Reject Repair Request, Cancel Repair Request, Close Work Order (Q1), Cancel Work Order. | `docs/13` §4.7 (Not in S1-008), §4.8 (Not in S1-009), §4.20 Q1, §4.25 |
| F16 | The timeline is complete (UC-WO-002 / AUD-API-001, PR #22, merge commit `9eb05a7e…`); its documented limits (WORK_ORDER only, no read audit, status filter only on the list) and the recorded performance caveat "O1" of `docs/15` §13 (BIGIDS first-page tempdb spill) stay as documented. | `docs/15` §13; `docs/13` §4.26 |
| F17 | Baseline UI-031 (Work Order Detail) lists *Overview/Visits/Summary/Cost/Acceptance/Timeline* for *Coordinator/Team Lead/Supervisor*. The Service Report is an owner-defined record with its own access rule (OD5, §4.1); the existing Work Order detail policies are unchanged (`WorkOrder.Read` excludes Technician — DEC-S2-001-03; `WorkOrder.ReadWorkSummary` = Technician, Team Lead, Supervisor; `CostSummary.Read` = Team Lead, Supervisor). | `docs/10` p.2 (UI-031); `docs/12` §11 DEC-S2-001-03; `docs/13` §3.1 |

### 2.1 What the code does today (`main` at `9eb05a7e…`, read-only inspection)

| Area | Current implementation |
|---|---|
| Work Summary | `work_summary` table; `WSM-API-001/002`, `GET /work-orders/{id}/work-summary` (latest revision); request body `{ summaryText, repairOutcomeCode }` — **no evidence field**. Read policy `WorkOrder.ReadWorkSummary` = Technician, Team Lead, Supervisor (Requester excluded). |
| Evidence | **No evidence table, entity or endpoint exists** (`docs/13` §6 row "Work Summary submit / review"). |
| Files | `FILE-API-001` uploads only to a **Draft Repair Request** (`access_scope_code = REPAIR_REQUEST`); `FILE-API-002` serves only files attached to a Repair Request in scope and only when CLEAN (`docs/13` §4.2). No Work Order upload endpoint exists. |
| Malware scanning | Production keeps `NotConfiguredMalwareScanner`: files stay PENDING and unusable; the deterministic fake scanner is Development/Testing only (`docs/13` §4.2 "Not provided"; `docs/12` §8). A scanner result *Infected* is persisted as **FAILED** — the stored enum is PENDING / CLEAN / FAILED (FAS-008). |
| Work Session facts | Check-in/pause/resume/check-out data exists in `work_session` and `work_session_pause`; the only API that returns a session is the Technician's own current session (`GET /work-sessions/current`). `ServiceVisitResponse` carries no session times. |
| Customer Acceptance | `customer_acceptance` rows exist (rounds); the Work Order response exposes only `acceptanceContactId` (WO-008). |
| Cost Summary | Implemented end to end (`docs/13` §4.18–§4.20); read policy `CostSummary.Read` = Team Lead, Supervisor. |
| SLA | `repair_request.submitted_at` marker only; no `sla_record`, no SLA endpoint. |

---

## 3. Owner decisions D1–D8 (final wording)

**D1 — Service Report definition. OWNER DECISION.**
Service Report V1 is an **operational completion record** consisting of: the Work Summary; the outcome; the Evidence that has passed CLEAN status; the Service Visit / Work Session / Check-in / Check-out data that already exists; and, as related lifecycle sections, Customer Acceptance and Cost Summary. The Service Report is **not** `UC-REP-001` analytics/reporting, a Dashboard, an SLA report, or a printable/PDF document. No `ServiceReport` aggregate or table is created if the whole record can be assembled as a **read model** from existing data.

**D2 — V1 delivery channels. OWNER DECISION.**
V1 must provide the **Backend API** and the **Angular Web** client. Flutter/mobile and printable/PDF export are not part of V1.

**D3 — Evidence guard location. OWNER DECISION.**
Check-out stays a **pure state transition**, as in the existing approved deviation (`docs/13` §4.14). The Summary/Outcome/Evidence guard is **not** moved back onto Check-out. Evidence is validated at **Submit Work Summary**: the submission must carry evidence satisfying the approved minimum catalog (OD1); the evidence that passes the guard must be `CLEAN`; PENDING, infected and FAILED evidence cannot pass the guard; and the Work Summary submission together with evidence validation and binding must be protected against races and stale state.

**D4 — Evidence catalog. OWNER DECISION.**
The baseline does not define an evidence value catalog: `EVD-004` *"Active Evidence Type"* has no value list in any baseline document (§9). The owner-approved minimal V1 catalog is **OD1** (§10): `BEFORE_WORK`, `AFTER_WORK`, `OTHER`. No evidence implementation (table, migration, endpoint or guard) has started; it belongs to Ticket 4.

**D5 — SLA clock. OWNER DECISION.**
The SLA foundation uses elapsed time 24/7 in UTC, starting at Repair Request Submit; `dueAt = startedAt + 24 hours`; `riskAt = dueAt − 4 hours`. No working calendar, holiday calendar or Site time zone is used in Sprint 5.

**D6 — SLA stop events. OWNER DECISION.**
The SLA stops at the **first** qualifying event: Repair Request rejected; Repair Request cancelled; Work Order closed; Work Order cancelled. Stopping is **idempotent** and records `stoppedAt` and `stoppedReason`. An SLA record that has stopped is never modified.

**D7 — Historical data. OWNER DECISION.**
No migration creates SLA history retroactively by guessing. Existing data without an `sla_record` is shown by the API/read model as `NOT_TRACKED_LEGACY`: it is not a breach, creates no notification and does not affect existing use. A future backfill, if wanted, is a separate owner-approved operation.

**D8 — Sprint 5 exclusions. OWNER DECISION.**
Excluded from Sprint 5: SLA override; the risk/breach worker; notification/outbox; `UC-REP-001`; report/export/PDF; Flutter; approval inbox; dashboard; Time Correction; additional timeline entity types; the production malware provider; historical SLA backfill.

---

## 4. Service Report composition (D1, OD4, OD5)

The Service Report is a **read model** assembled per Work Order. It introduces no new aggregate; the only new persistent data in Sprint 5 are the evidence records (Ticket 4) and the SLA record (Ticket 3), both required by the baseline (F6, F13).

| Section | Content | Source data | Exists today | Notes |
|---|---|---|---|---|
| Header | Work Order no./status, Repair Request no., Customer/Site/Equipment codes, closed/cancelled timestamps | `work_order`, `repair_request`, master data | Yes (`WorkOrderResponse`) | codes only, as today (`DEC-S2-001-01`) |
| Service Visits | per Visit: type, status, schedule, assignee, completed time, missed/cancel facts | `service_visit` | Yes (`WorkOrderResponse.visits`) | corrective Visits included |
| Work Sessions | per Visit: check-in, pauses (start/resume), check-out | `work_session`, `work_session_pause` | Data yes; **no API** except the Technician's own current session | Ticket 4 adds the read path; no new table |
| Work Summary & outcome | summary text, `repairOutcomeCode`, revision per Visit | `work_summary` | Yes (latest only via `GET …/work-summary`) | how revisions appear is fixed in the Ticket 4 plan |
| Evidence | evidence type, file name/MIME/size, scan status, uploader/time; **content only when CLEAN**; the set **bound to the submitted Work Summary** (OD1) | new `work_summary_evidence` + `file_asset` | **No** (Ticket 4) | EVD-001/003/004/013 (F6); frozen at submit |
| Customer Acceptance (related lifecycle) | rounds: decision, reason (Reject), decided time, contact | `customer_acceptance` | Data yes; **no API** | included in the report; only the Cost Summary is excluded for the Technician and the Acceptance Contact (OD5) |
| Cost Summary (related lifecycle) | total, currency, note, prepared/reviewed by/at | `cost_summary` | Yes (`GET …/cost-summary`) | **Team Lead and Supervisor only** (OD5, §4.1) |

**Out of the Service Report:** `UC-REP-001` reports and exports, Dashboard, SLA reports, PDF/print, Corrective Action plan text/file (hidden by existing decisions, `docs/13` §4.21 Decision (b)), raw audit JSON, e-mail addresses.

**Design constraints from existing evidence** (settled in the Ticket 4 plan, within OD1–OD5): the read model applies tenant/Site scope; it is bounded (no unbounded graph load — `docs/04` §5 performance appendix; `docs/07` performance appendix p.1 "Summary / Evidence"); and it is measured against the p95 ≤ 3.0 s target (`docs/11`). The API ID and route of the Service Report endpoint are **defined in Ticket 4**; this sheet names none.

### 4.1 Service Report access matrix (OD5) — OWNER DECISION

| Actor | How the actor is identified (existing rule) | Operational Service Report | CLEAN evidence content | Cost Summary values |
|---|---|---|---|---|
| Assigned/owning **Technician** | Technician assigned to a Visit of the Work Order (`service_visit.assigned_technician_id`; `IDataScope.AssignedServiceVisits`, `docs/13` §4.11) | Yes | Yes | **No** |
| In-scope **Team Lead** or **Supervisor** | tenant + Site scope (`IDataScope.WorkOrders`) | Yes — full report | Yes | **Yes** |
| **Exact Customer Acceptance Contact** of the Work Order/Repair Request | the caller is exactly the contact designated on the Work Order (`work_order.acceptance_contact_id`, WO-008, designated through `WSM-API-002`; `docs/13` §4.16) | Yes — **read-only**; CLEAN evidence is what Accept/Reject relies on | Yes | **No** |
| **Requester or Site Contact who is not** the exact Acceptance Contact | — | **No** — the role alone grants nothing | No | No |
| Every other role or actor (e.g. Coordinator, Approver, Administrator) | — | Not granted by OD5 | No | No |

**Enforcement.** A **specific backend policy and scope rule** for the Service Report (named in the Ticket 4 plan) — the existing `WorkOrder.Read` policy is **not** assumed to provide this matrix (F17). Authorization and data scope are server-enforced; Angular route/section guards are UX only. Out-of-scope and unauthorized access follows the repository's non-leaking 403/404 conventions (`docs/13` §3.2); the exact responses are fixed in the Ticket 4 plan.

**No-regression clarification.** “The Service Report uses its own authorization policy. This decision does not remove, narrow or replace the Coordinator’s existing access to the current Work Summary/Cost Summary endpoints or UI-031 surfaces. Ticket 4 must preserve those existing permissions and regression-test them.”

### 4.2 No hidden-cost disclosure (OD5)
For the Technician and the Acceptance Contact the Cost Summary section is **omitted entirely**: the response carries no amount, currency, note, total, prepared/reviewed identity or timestamp, and no cost-derived DTO field, metadata, existence flag or attachment data. Ticket 4 proves this with tests over the response shape for every actor in §4.1.

---

## 5. V1 scope

| In V1 (D2) | Status on `main` (`9eb05a7e…`) |
|---|---|
| Repair Request lifecycle: Draft, Submit, Approve/Reject/Return, Cancel, Convert (API) | Implemented (`docs/13` §1) |
| Work Order: Schedule, Visit management, Check-in/Pause/Resume/Check-out | Implemented (API; Angular My Visits / active session) |
| Work Summary submit/review | Implemented without evidence |
| Customer Acceptance (Accept/Reject), Corrective Action cycle, re-acceptance | Implemented |
| Cost Summary prepare/review, Work Order Close, Cancel Work Order | Implemented (SLA stop pending, Ticket 3) |
| Work Order timeline (UC-WO-002 / AUD-API-001) | **Done** — Ticket 1, PR #22 |
| Work evidence persistence/binding + dedicated evidence APIs + CLEAN guard at Submit Work Summary | Not implemented — Ticket 4 |
| Service Report read model, Backend API and Angular section | Not implemented — Ticket 4 (OD4) |
| SLA record foundation (new data) | Not implemented — Ticket 3 |
| Angular Web | Existing screens unchanged (code inspection of `frontend/src/app/app.routes.ts`: Work Order list/detail, My Visits, active Work Session, Work Summary review, Cost Summary review, Repair Request list/detail). Sprint 5 adds the **Service Report section** only (Ticket 4). |

| Out of V1 / Sprint 5 | Why |
|---|---|
| Flutter / mobile | D2 |
| Printable / PDF export, report/export (`UC-REP-001`) | D1, D2, D8 |
| Notifications / outbox, SLA risk/breach worker, SLA override | D8 |
| Dashboard, approval inbox | D8 |
| Time Correction (`UC-TIME-001`; exists only on the unmerged holding branch) | D8 |
| Additional timeline entity types | D8 |
| **Production malware provider** — a configured production scanner is a separate release-readiness gate | D8, OD3 |
| Historical SLA backfill | D7, D8 |
| New **login**, **Repair Request create/submit/cancel** and **approval-decision** Angular screens | OD7 — existing implementations remain unchanged; the missing UI coverage is later backlog, not a V1 or Sprint 5 blocker |

---

## 6. Sprint 5 backlog — exactly 4 tickets

Ticket names are owner-approved labels, not baseline IDs. Sizes are **OWNER-PROPOSED** estimates.

| # | Ticket | Status | Size | Depends on |
|---|---|---|---|---|
| 1 | UC-WO-002 / AUD-API-001 — Work Order Timeline & Audit | **DONE** (PR #22, merge `9eb05a7e…`) | — | — |
| 2 | V1 Scope + Service Report Decision + Docs Reconciliation (this sheet; `docs/12`, `docs/13`, `docs/15`) | **In review** (docs only) | S | 1 |
| 3 | SLA Record Foundation | Not started | M | 2 |
| 4 | Work Evidence + CLEAN Evidence Guard + Service Report | Not started | L | 2 |

**Completed 1 / 4.** No fifth ticket is created.

**Ticket 3 scope (D5–D7, D8, OD6).** A `sla_record` is created for each **new** Repair Request Submit with `startedAt`, `originalDueAt = startedAt + 24 h`, `riskAt = dueAt − 4 h` (UTC) and `sla_policy_version = 'SLA-004/v1'` (OD6); the SLA stops (once, immutably, with the reason REQUEST_REJECTED / REQUEST_CANCELLED / WO_CLOSED / WO_CANCELLED) when the first of Reject Repair Request, Cancel Repair Request, Close Work Order or Cancel Work Order occurs; a read endpoint (`SLA-API-001`, `docs/09` p.4) returns the record or `NOT_TRACKED_LEGACY`. Excluded: override, worker, breach evaluation, notification, backfill. Anything the owner decisions do not state (§10.2) is not adopted.

**Ticket 4 scope (D1, D3, D4, OD1–OD5).**
1. Evidence persistence and **binding to the submitted Work Summary**, and **dedicated Work Order Evidence APIs** (new API IDs and routes defined in the ticket plan) for upload, metadata/list and CLEAN-file download — `FILE-API-001/002` are not reused.
2. The CLEAN guard inside Submit Work Summary.
3. The Service Report read model and Backend API, with the access matrix of §4.1.
4. The Angular Service Report section.
5. Authorization, data-scope, concurrency/race tests and regressions.

---

## 7. Dependency graph

```
Ticket 1 (DONE) ──► Ticket 2 (this sheet) ──┬──► Ticket 3  SLA Record Foundation ─────────────────────┐
                                            │                                                          ├──► V1 scope-complete (§13)
                                            └──► Ticket 4  Evidence + CLEAN Guard + Service Report ────┘
                                                                                                     │
                              configured production malware scanner (separate gate, future decision) ┴──► production-ready (§13)
```

Ticket 4 depends only on Ticket 2: OD1–OD5 are closed (§10). Tickets 3 and 4 are independent of each other (different tables and flows); both add an additive EF migration, so their migrations are ordered when they merge. Inside Ticket 4 the natural order is evidence persistence → dedicated APIs → Submit guard → Service Report read model/API → Angular section.

---

## 8. Acceptance criteria

### Ticket 2 (this sheet)
- Only `docs/12`, `docs/13`, `docs/15` and `docs/16` change; no baseline PDF, source, test, migration or configuration file.
- `docs/16` has the thirteen requested sections; D1–D8 and OD1–OD7 are recorded in the owner's meaning; no owner decision is left open; every statement has a source or an OWNER tag; Sprint 5 lists 4 tickets with Ticket 1 DONE and Ticket 4 named "Work Evidence + CLEAN Evidence Guard + Service Report".
- `docs/13` header, §1 and §6 no longer describe merged work as "working tree"; `WO-API-002..007` detail matches the implementation (§4.10A); `docs/12` has a pointer to this sheet; `docs/15` Document Control records that AUD-API-001 was committed and merged through PR #22 at `9eb05a7e…` without touching its decisions or Phase 1 evidence; no approved deviation is removed.
- `git diff --check` clean; no secret, generated output or attribution text.

### Ticket 3 — SLA record foundation
- A new Submit creates exactly one `sla_record`, atomically with the Submit, for new data only; times come from the server clock in UTC; `dueAt = startedAt + 24 h`, `riskAt = dueAt − 4 h`; `sla_policy_version = 'SLA-004/v1'`; no calendar and no Site time zone.
- The first of Reject Repair Request, Cancel Repair Request, Close Work Order, Cancel Work Order stops the SLA with the matching reason and `stoppedAt`; a later event changes nothing and raises no error (idempotent); a stopped record is never updated (domain guard plus test).
- Concurrent Submit/stop commands cannot create a duplicate record or stop twice. The mechanism follows the repository's concurrency/idempotency conventions where a mutable resource or state transition needs them; the exact behaviour is documented in the Ticket 3 plan.
- Existing data without `sla_record` returns `NOT_TRACKED_LEGACY` — no breach, no notification, no behaviour change, **no migration backfill**.
- Authorization/scope follow `IDataScope`; foreign or missing ids return the existing non-leaking responses.
- Additive migration; unit, API and integration tests (incl. concurrency and legacy rows); full regression and CI green; `docs/13` §1/§6 and `docs/12` updated; query plan checked against p95 ≤ 3.0 s.

### Ticket 4 — Work Evidence + CLEAN Evidence Guard + Service Report
- **Evidence model.** An additive migration creates the evidence persistence (EVD-001/003/004/013). `evidence_type_code` is a closed allowlist `BEFORE_WORK`, `AFTER_WORK`, `OTHER`, enforced by a database `CHECK` generated from the same list. At most **10** evidence items per Work Summary. Evidence items are **bound to the Work Summary at submission**; once bound the set is frozen — later uploads or changes cannot alter a submitted report.
- **Dedicated APIs.** New API IDs and routes for Work Order evidence upload, metadata/list (bounded, paged) and CLEAN-file download; `FILE-API-001` and `FILE-API-002` are not reused or changed. Upload is allowed only to the assigned/owning Technician, only while the Work Order is `IN_PROGRESS` and before the Work Summary is submitted. File rules are the baseline's (PDF/JPG/JPEG/PNG, ≤ 10 MB, extension/content type/signature agree, SHA-256, private storage; FAS-003..010). Every new file starts `PENDING`. File content is served **only when CLEAN**; PENDING and FAILED (including infected) files are not downloadable as evidence.
- **Guard.** Submit Work Summary succeeds only when the submitted evidence set contains **at least one `CLEAN` item** (any catalog type, including `OTHER`, satisfies the minimum), is within the maximum, and every referenced file is in the caller's tenant and Work Order scope; validation and binding run in **one transaction** with the Work Summary write. PENDING and FAILED (infected) files cannot satisfy the guard; the rejection is the repository's validation error with no state change. A stale or concurrent submit cannot double-submit, bind one file twice, or pass a file that was not CLEAN at commit. The existing Submit Work Summary concurrency contract is retained; any new mutable resource follows the repository's concurrency/idempotency conventions, with the exact behaviour documented in the Ticket 4 plan.
- **Check-out unchanged** (regression test: no summary/outcome/evidence requirement on Check-out).
- **Existing permissions preserved.** The Service Report uses its own policy; the Coordinator’s existing access to the current Work Summary/Cost Summary endpoints and UI-031 surfaces is not removed, narrowed or replaced, and is covered by regression tests (§4.1).
- **Service Report.** A read model and Backend API with the sections of §4, applying the access matrix of §4.1 through a specific backend policy and scope rule (the existing `WorkOrder.Read` is not assumed to provide it); no cost-derived data for the Technician and the Acceptance Contact (§4.2, shape tests per actor); bounded query; p95 ≤ 3.0 s check.
- **Angular.** A Service Report section for the permitted actors (role-based visibility is UX only), covering loading, empty, error, forbidden/not-found and evidence-download states; existing screens are otherwise unchanged (OD7).
- **Tests.** Unit, API, integration and Angular tests, including: the full per-actor matrix (assigned vs unassigned Technician; in-scope vs out-of-scope Team Lead/Supervisor; exact vs non-exact Acceptance Contact; Requester/Site Contact who is not the contact; other roles); evidence catalog, maximum, minimum-CLEAN and freeze rules; PENDING/CLEAN/FAILED(infected) behaviour proven with the **deterministic test scanner**; race and stale-state tests for Submit with evidence; regressions of Check-out, Work Summary review, Accept/Reject, Close.
- **Production statement.** Acceptance uses the deterministic test scanner only. Ticket 4 does **not** claim that the production CLEAN path works (§13).
- Additive migration; full regression and CI green; `docs/13` §1/§6 and `docs/12` updated.

---

## 9. Evidence catalog discovery table (D4)

| Evidence concept | Baseline field / value | Required / optional | Supported MIME / file type | Scan requirement | Owner / source | Existing implementation | Ambiguity / resolution |
|---|---|---|---|---|---|---|---|
| Evidence record | `EVD-001 evidence_id` UUID PK (`docs/05` p.6) | structural | — | — | RR-DD-001; ERD `work_summary_evidence` (`docs/07` p.2–3) | none | The dictionary lists only EVD-001/003/004/013 |
| Link to Work Summary | `EVD-003 work_summary_id` FK (`docs/05` p.6); `WSM-005 revision_no` unique per Visit | required | — | — | RR-DD-001 | `work_summary` exists, no evidence link | **Resolved by OD1/OD2:** evidence is bound to the Work Summary at submit and frozen |
| Evidence type | `EVD-004 evidence_type_code varchar(30)` *"Active Evidence Type"* (`docs/05` p.6); test master "Evidence Type" (`docs/06` p.2) | required | — | — | **no value list in any baseline document** (searched `docs/00`–`docs/11`, RR-ARCH-001) | none | **Resolved by OD1:** closed catalog `BEFORE_WORK`, `AFTER_WORK`, `OTHER` (§9.1) |
| File | `EVD-013 file_asset_id` *"FK CLEAN FileAsset"* (`docs/05` p.6) | required | PDF / JPG / JPEG / PNG, ≤ 10 MB (`FAS-004/005`, `docs/05` p.4; `docs/01` p.5) | CLEAN only (`FAS-008`; RR-ARCH-001 §11) | RR-DD-001 | `file_asset` + validation rules exist (`AttachmentFileRules`) | The rules are applied only in the Repair Request upload today; Ticket 4 applies the same rules in its dedicated contract |
| Scan state | `PENDING / CLEAN / FAILED` (`FAS-008`) | — | — | only CLEAN satisfies mandatory evidence; PENDING/FAILED never usable (`docs/02` performance appendix p.1 §6; `docs/10` p.4; TC-WO-007) | RR-REQ-001 §9 | enum implemented; an *Infected* scan result is stored as FAILED; Production scanner not configured | **OD3:** test scanner for acceptance; production scanner is a separate gate |
| Mandatory rule | BR-06 *"required CLEAN evidence"*; *"category-required CLEAN evidence; corrective resubmission always requires CLEAN evidence"* (`docs/01` p.3, `docs/02` p.3) | required | as above | CLEAN | BR-06; ST-WO-003 guard (`docs/03` p.3) | not implemented | **Resolved by OD1:** at least one CLEAN item per submitted Work Summary, applied to every submission including rework revisions; Repair-Request-Category rules stay deferred (`docs/12` §8) |
| Guard location | Baseline gives both Check-out (ST-SV-003, UC-WO-016, FR-06) and ST-WO-003 | — | — | — | **D3 (owner)**: Submit Work Summary | Check-out already a pure transition | Resolved by D3 |
| Uploader / actor | `FAS-009 uploaded_by` derived actor (`docs/05` p.4); UI-041 lists evidence under the Technician Work Session screen (`docs/10` p.2) | derived | — | — | RR-DD-001; RR-UI-001 | none for Work Orders | **Resolved by OD2:** assigned/owning Technician, Work Order `IN_PROGRESS`, before submission. (The relationship catalog in `docs/07` p.3 contains a rule text "source role REQUESTER/SITE_CONTACT only" whose row is scrambled in the PDF text; it is not used as evidence for evidence upload.) |
| Upload path | UI-041 *"…Check-out/evidence"* (`docs/10` p.2); `FILE-API-001` (`docs/09`) | — | — | — | RR-UI-001; RR-API-001 | **only** `POST /repair-requests/{id}/attachments` (Draft RR owner; scope `REPAIR_REQUEST`) | **Resolved by OD2:** a dedicated Work Order Evidence contract with new API IDs and routes (Ticket 4); `FILE-API-001/002` not reused |
| Download | `FILE-API-002` *"Authorized download"* (`docs/09` p.4) | — | — | CLEAN only | RR-API-001 | only for files attached to an in-scope Repair Request (`docs/13` §4.2) | **Resolved by OD2:** dedicated CLEAN-only download in Ticket 4 |
| Count / bound | none stated | — | — | — | — | — | **Resolved by OD1:** maximum 10 per Work Summary (also a bounded-input performance rule, `docs/04` §5 performance appendix) |
| Corrective evidence | UC-WO-023 *"Plan text + CLEAN evidence"* (plan file, implemented via `planFileAssetId`, `docs/13` §4.21); UC-WO-024 *"evidence required"* | required | as above | CLEAN | `docs/04` p.11 | plan file CLEAN check implemented; rework evidence not | Rework summary resubmission uses the same Work Summary flow (`docs/13` §4.24) and therefore the same guard |

### 9.1 Owner-approved minimal V1 evidence catalog (OD1) — OWNER DECISION
1. **Closed type allowlist, three values, normalized upper-case, stored in `evidence_type_code` (`EVD-004`):** `BEFORE_WORK`, `AFTER_WORK`, `OTHER`.
2. **Minimum rule:** at least one evidence item bound to each submitted Work Summary must be `CLEAN`. Any approved type, including `OTHER`, may satisfy the minimum.
3. **Maximum:** 10 evidence items per Work Summary.
4. **Deferred:** per-type minimums and stronger category rules (including Repair-Request-Category evidence policy) remain deferred (`docs/12` §8).
5. **Binding:** the evidence set used by a submitted Work Summary is bound and frozen so later uploads cannot silently change the submitted report.

---

## 10. Owner decisions OD1–OD7 — closed (11 October 2026)

| # | Decision (final wording) | Source cross-check |
|---|---|---|
| **OD1** | The minimal evidence catalog is **approved**: `BEFORE_WORK`, `AFTER_WORK`, `OTHER`. At least one evidence item bound to each submitted Work Summary must be CLEAN; any approved category, including `OTHER`, may satisfy the minimum; maximum 10 evidence items per Work Summary; per-category minimums and stronger category rules remain deferred; the evidence set used by a submitted Work Summary is bound/frozen so later uploads cannot silently change the submitted report. | Consistent with F2 (CLEAN evidence; corrective resubmission always CLEAN), F6 (EVD-003 binds evidence to one Work Summary). "Category" here = evidence type; the baseline's "category-required" = Repair Request Category and stays deferred (F11). |
| **OD2** | A **dedicated Work Order Evidence contract**: `FILE-API-001/002` are not reused. Ticket 4 defines new API IDs and routes for Work Order evidence upload, metadata/list and CLEAN-file download. Only the assigned/owning Technician may upload, while the Work Order is `IN_PROGRESS` and before Work Summary submission. Submit Work Summary validates and binds the evidence set transactionally. File content may be downloaded only when its scan status is CLEAN; PENDING/INFECTED/FAILED files may not satisfy the guard and may not be downloaded as evidence. Authorization and data scope are server-enforced; Angular guards are UX only. | Consistent with F3, F8, F9, F12 (UI-041). "Assigned Technician" is derived from `assigned_technician_id` (`docs/13` §4.11). "INFECTED" is not a stored status: an infected scan result is persisted as FAILED (FAS-008 enum PENDING/CLEAN/FAILED). |
| **OD3** | The **production malware provider stays excluded**. Automated acceptance may use the deterministic test scanner to prove CLEAN/PENDING/INFECTED/FAILED behaviour. Sprint 5 can become **V1 scope-complete but not production-ready** while Production uses `NotConfiguredMalwareScanner` and files remain PENDING. A configured production malware scanner is a separate release-readiness gate and a future owner decision. Sprint 5 does not claim that the production CLEAN path works. | Consistent with D8 and `docs/12` §8 (provider deferred; `docs/13` §4.2). |
| **OD4** | The **Service Report belongs in Ticket 4**, renamed "Work Evidence + CLEAN Evidence Guard + Service Report": evidence persistence/binding and APIs; the Work Summary Submit CLEAN guard; the Service Report read model and Backend API; the Angular Service Report section; related authorization, scope, concurrency/race tests and regressions. No fifth Sprint 5 ticket. | Consistent with D1 (read model) and D2 (API + Angular). |
| **OD5** | **Service Report access matrix** (§4.1): assigned/owning Technician — operational report and CLEAN evidence, no Cost Summary values; in-scope Team Lead or Supervisor — full report including Cost Summary; exact Customer Acceptance Contact of the Work Order/Repair Request — read-only report and CLEAN evidence required for Accept/Reject, no Cost Summary values; a Requester or Site Contact who is not the exact Acceptance Contact receives no access merely because of that role. Enforced by a specific backend policy/scope rule; the existing `WorkOrder.Read` policy is not assumed to provide the matrix. The report must not disclose hidden cost fields through totals, DTO fields, metadata or attachment data. | The exact contact is `work_order.acceptance_contact_id` (WO-008; `docs/13` §4.16). Not a conflict with F17: the Service Report has its own rule and the existing detail policies are unchanged. |
| **OD6** | `sla_policy_version` = **`SLA-004/v1`**. Only the SLA behaviour already locked by D5–D7 is confirmed; nothing is inferred from NB-9 or other documents; conflicting or additional evidence is listed as unresolved, not adopted (§10.2). | The `sla_policy_version` column is `SLA-004` in the dictionary (`docs/05` p.8, `varchar(30)`); the literal `SLA-004/v1` (10 characters) fits. |
| **OD7** | Extra Angular workflows are **outside Sprint 5**: no new login, Repair Request create/submit/cancel or approval-decision screens. Existing implementations remain unchanged. Missing UI coverage is recorded as later backlog, not as a V1 or Sprint 5 blocker. | Consistent with D8 (approval inbox, dashboard excluded). |

### 10.1 Source-evidence cross-check (rule: stop only on a direct contradiction)
Each decision was compared with the baseline and the code. **No direct contradiction was found**; the wording notes are recorded in the table above and in F11, F17 and §2.1: the two meanings of "category"; "INFECTED" being stored as FAILED; the baseline UI-031 tabs versus the Service Report access rule; and the derivation of "assigned Technician" and "exact Acceptance Contact" from existing rules.

### 10.2 Evidence noted and not adopted (OD6)
| Item | Source | Treatment |
|---|---|---|
| NB-9 (a): the PDFs' "approval pending" wording is superseded for governance classification by DEC-PS1-016 | `docs/12` §9 NB-9 | listed; no effect on D5–D7; not adopted as SLA behaviour |
| NB-9 (b): a working-hours/calendar model would differ from BR-12 "24/7" and would need a Change Request | `docs/12` §9 NB-9 | listed; consistent with D5 (no calendar); nothing further adopted |
| NB-9 (c): escalation beyond NTF-SLA-RISK / NTF-SLA-BREACH is not defined in the baseline | `docs/12` §9 NB-9 | listed; unresolved in the baseline; notification and worker are excluded (D8) |
| Resubmission after Return for Correction: the SLA continues with the original `submitted_at` (DEC-PRE-S1-010-02); one `sla_record` per Repair Request (`SLA-003` unique FK) | `docs/12` §8/§9 NB-3; `docs/05` p.8 | existing approved decision outside D5–D7; **not extended or adopted here**; D5–D7 do not state the resubmission behaviour — the Ticket 3 plan must show how it relates to D5 and to DEC-PRE-S1-010-02 |
| `SLA-007 effective_due_at`, `SLA-009 risk_notified_at`, `SLA-010 breached_at`, sticky-breach and override behaviour | `docs/05` p.8; `docs/03` p.4 (EV-SLA-002..004) | belong to the worker/override/notification scope excluded by D8; not adopted; the Ticket 3 plan states which columns exist |

### 10.3 Previously recorded deferrals (unchanged)
REQ-FU-USR-001 user lifecycle (`docs/12` §8.1); Approve Plan separation of duties (`docs/13` §4.21 Decision (d)); Visit-scheduling overlap guard (`docs/13` §4.22 Decision (d)); timeline read-audit and other timeline entity types (`docs/15`).

---

## 11. Traceability impact

| Ticket | Baseline IDs touched | Documents updated when delivered |
|---|---|---|
| 2 | — (documentation only) | `docs/12` §13 pointer; `docs/13` header, §1, §4.10A, §6; `docs/15` Document Control |
| 3 | FR-08; BR-12; EV-SLA-001/005; SLA-001..012; UC-SLA-001 (start/stop part); UC-RR-002, UC-RR-004, UC-WO-026, ST-RR-002/005/007, ST-WO-006/011; `SLA-API-001`; TC-SLA-001 (part) | `docs/13` §1, new section, §6; `docs/12` §8 deferred rows |
| 4 | FR-06/07/09; BR-06; ST-WO-003/004; UC-WO-016/020/021/024; EVD-001/003/004/013; WSM-005..007; FAS-001..010; TC-WO-007/008; UI-041/050; new technical API IDs for Work Order evidence upload/list/download and for the Service Report endpoint — **defined in Ticket 4** | `docs/13` §1, new sections, §6; `docs/12` §8 (category-evidence row) |

---

## 12. Risks and deferred work

| # | Risk / deferred item | Treatment |
|---|---|---|
| R1 | Production uses `NotConfiguredMalwareScanner`, so files stay PENDING and the production CLEAN path cannot work in Sprint 5 | OD3: acceptance with the test scanner; a configured production scanner is a separate release-readiness gate and a future owner decision |
| R2 | Evidence upload/download has no Work Order contract today | closed by OD2; Ticket 4 defines it |
| R3 | Two additive migrations (Tickets 3, 4) | order at merge; no backfill (D7) |
| R4 | The Service Report read model joins Visits, Sessions, Summaries, Evidence, Acceptances and Cost, with a per-actor visibility rule | bound the query; measure p95 ≤ 3.0 s (`docs/11`); shape tests per actor (§4.2) |
| R5 | Known performance risk "O1" of `docs/15` §13 (timeline BIGIDS first-page tempdb spill) | stays recorded there; not changed here |
| R6 | `docs/15` Document Control status was stale | corrected in this revision (statuses only; decisions and evidence untouched) |
| R7 | Missing Angular coverage of login, Repair Request create/submit/cancel and approval decision | OD7: later backlog, not a Sprint 5 or V1 blocker |
| R8 | Deferred as before: Time Correction, SLA override/worker, notification/outbox, reports, approval inbox, dashboard, Flutter, PDF | D8 |

---

## 13. Completion terminology and Definition of V1 Complete

**V1 scope-complete** means **all seven** of the following hold:
1. The **Service Report** defined by D1 is usable through the **API and the Angular Web** client, with the access matrix of §4.1.
2. **Work Summary** submission and the **CLEAN Evidence guard** (D3, OD1, OD2) pass.
3. **Cost Summary** prepare and review pass.
4. **Customer Acceptance** (Accept/Reject) passes.
5. **Work Order Close** passes.
6. The **SLA foundation** (D5–D7, OD6) works for new data.
7. Automated regression and **CI** pass.

**Production-ready** additionally requires a **configured production malware scanner**. That is a separate release-readiness gate and a future owner decision (OD3); it is **not** part of Sprint 5. While Production uses `NotConfiguredMalwareScanner`, files remain PENDING, and Sprint 5 does not claim that the production CLEAN path works.

**Outside Sprint 5 and not required for V1 scope-complete:** Flutter/mobile, PDF/print export, reports (`UC-REP-001`), notifications/outbox, the SLA monitor/worker and SLA override, dashboard, approval inbox, Time Correction, additional timeline entity types, historical SLA backfill, and the new Angular login / Repair Request create-submit-cancel / approval-decision screens (D8, OD7).

---

## Appendix — Verification record of this sheet
- Sources: extracted text of `docs/00`–`docs/11` and RR-ARCH-001 (printed page footers), `docs/12`, `docs/13`, `docs/15`, and a read-only inspection of the code on `main`.
- Sprint 5 count: 4 tickets, Ticket 1 DONE (1/4); Ticket 4 is named "Work Evidence + CLEAN Evidence Guard + Service Report".
- Nothing in this sheet is implemented by Ticket 2; the only OWNER-PROPOSED items left are the Sprint 5 size estimates (§6).
