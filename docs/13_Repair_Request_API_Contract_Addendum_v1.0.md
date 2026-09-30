RR-API-001-ADD

# Repair Request — API Contract Addendum (Sprint 1 Implemented Endpoints)

Companion to RR-API-001 v1.2. It documents what is implemented, including the Pre-S1-007 contract amendments implemented in S1-007.

## Document Control

| Field | Value |
|---|---|
| Document ID | RR-API-001-ADD |
| Version | 1.0 |
| Status | Approved for Portfolio Development (documentation reconciliation) |
| Revision Date | 29 September 2026 — Ticket 6 Corrective Action Submit Plan/Approve Plan decisions and implementation (working tree — see §4.21); previously 28 September 2026 — Ticket 5 Work Order Close decisions and implementation (PR #15, commit d6c4ca9); previously 24 September 2026 — Sprint 4 round decisions recorded ahead of implementation (Work Summary Submit/Review, Customer Accept/Reject, Work Order Close; as of 24 September 2026 no code existed yet — historical status at that revision, since superseded, see §4.15); previously 23 September 2026 (S3-004 Check-out Work Session; working tree, not yet committed), 22 September 2026 (S3-003, PR #8, commit 459501a), 19 September 2026 (S3-002, PR #7, commit db6bc5b), 18 September 2026 (S3-001) and 17 September 2026 (S2-001) |
| Extends | RR-API-001 v1.2 (PDF, unchanged) |
| Decision source | RR-DEC-001 v1.2 (`12_Pre_Sprint1_Baseline_Decision_Register_v1.0.md`), Section 11 for S2-001; Portfolio Project Owner pre-implementation directives for S3-001 (eligibility is `assigned_technician_id` only — no "Primary team"; location capture out of scope) for My Visits / Check-in; Portfolio Project Owner scope decision for S3-004 (Check-out is a pure status transition — BR-06's summary/outcome/evidence half is a later ticket); Portfolio Project Owner directives for the Sprint 4 round (UC-WO-020 traceability correction, Technician/Team-Lead-Supervisor/Customer-Acceptance-Contact role split, WO Close guard trimmed — see §4.15) |
| Implementation evidence | S1-002 (d2e96c5), S1-003 (7a4be91), S1-004 (c03c4c4), S1-005 (46f12a6), S1-006 (ce69f6e), S1-007 (36ffc6a), S1-007R (b1cad73), S1-008 (a45bc38), S1-009 (305079f), S1-010 (5064c6b), S2-001 (cdfc0a8), S2-003 Schedule/Service Visit management (9a05ca0, PR #5) — WO-API-002..007 implemented but **not yet reconciled into this addendum's §1/§4** (pre-existing gap, out of scope of this revision), S3-001 My Visits / Check-in (ee115a8, PR #6 — see §4.11), S3-002 Pause Work Session (db6bc5b, PR #7 — see §4.12), S3-003 Resume Work Session (459501a, PR #8 — see §4.13), S3-004 Check-out Work Session (a5a9bcb, PR #9 — see §4.14), Ticket 1 Work Summary submit/review (cfa09c7, PR #10 — see §4.15), Ticket 2 Customer Accept and Acceptance Contact lookup (d4ca55c, PR #11 — see §4.16), Ticket 3 Customer Reject and Corrective Action draft (eb4eea8, fix bff7280, PR #12 — see §4.17), Ticket 4a Cost Summary Prepare (c48659f, PR #13 — see §4.18), Ticket 4b Cost Summary Read/Review (30246d3, PR #14 — see §4.19), Ticket 5 Work Order Close (working tree, not yet committed — see §4.20) |
| Approval | Portfolio Project Owner Approval (DEC-PS1-016) |

**Rules for this addendum:**
- The RR-API-001 PDF is not modified.
- An endpoint is listed as implemented only if a controller route exists in the codebase as of the stated revision (S1-010, then S2-001).
- Addendum IDs (`AUTH-API-*`, `CUST-API-*`, `SITE-API-*`, `EQP-API-*`, `FILE-API-003`, `SYS-API-*`, `WO-API-ADD-*`) are documentation numbers, not new business semantics.
- Section 5 lists the approved Pre-S1-007 amendments to catalogued endpoints. All of them are **implemented in S1-007**; implementation details are in §4.1.
- Nothing here adds a business rule. Behaviour is traced to RR-DEC-001 decisions and baseline IDs.

---

## 1. Implementation Status of the RR-API-001 v1.2 Catalog (Sprint 1 scope)

| ID | Method / Path | Status at S1-007 |
|---|---|---|
| RR-API-001 | POST `/api/v1/repair-requests` | IMPLEMENTED (S1-005; S1-007 adds Category, Priority and request contact) — `locationId` not accepted, see §4.1 |
| RR-API-002 | PATCH `/api/v1/repair-requests/{id}` | IMPLEMENTED (S1-005; S1-007 adds Category, Priority and request contact) — `locationId` not accepted, see §4.1 |
| RR-API-003 | GET `/api/v1/repair-requests` | NOT IMPLEMENTED |
| RR-API-004 | GET `/api/v1/repair-requests/{id}` | IMPLEMENTED (S1-005; S1-007 adds the new fields to the response) |
| RR-API-005 | POST `/api/v1/repair-requests/{id}/submit` | IMPLEMENTED (S1-007; resubmission after Return for Correction in S1-010 (5064c6b05ae22dd443498f9734368eaec61b8437)) — see §4.1, §4.9 |
| RR-API-006..007 | approve / reject | IMPLEMENTED — S1-008 (a45bc38); see §4.7 |
| RR-API-009 | cancel | IMPLEMENTED — S1-009 (305079f9b8bc2e9373330a9fa5a6a61994a64545); see §4.8 |
| RR-API-008 | POST `/api/v1/repair-requests/{id}/return-for-correction` | IMPLEMENTED — S1-010 (5064c6b05ae22dd443498f9734368eaec61b8437); see §4.9 |
| RR-API-010 | convert | NOT IMPLEMENTED |
| FILE-API-001 | POST `/api/v1/repair-requests/{id}/attachments` | IMPLEMENTED (S1-006) |
| FILE-API-002 | GET `/api/v1/files/{fileAssetId}` | IMPLEMENTED (S1-006) |
| WO-API-001 | GET `/api/v1/work-orders/{id}` | IMPLEMENTED (S2-001) — read-only List/Detail; see §4.10 |
| WO-API-002..007 | schedule / reassign / reschedule / cancel / mark-missed / missed-decision | IMPLEMENTED (S2-003, PR #5, commit 9a05ca0) — Coordinator-only; **not yet reconciled into this addendum's detail sections** (pre-existing documentation gap, out of scope of this S3-001 revision) |
| WS-API-001 | POST `/api/v1/service-visits/{id}/check-in` | IMPLEMENTED (S3-001, PR #6, commit ee115a8) — Technician-only; see §4.11 |
| WS-API-002 | POST `/api/v1/work-sessions/{id}/pause` | IMPLEMENTED (S3-002, PR #7, commit db6bc5b) — Technician-only, own CHECKED_IN session only, reason required; see §4.12 |
| WS-API-003 | POST `/api/v1/work-sessions/{id}/resume` | IMPLEMENTED (S3-003, PR #8, commit 459501a) — Technician-only, own PAUSED session only, no body; see §4.13 |
| WS-API-004 | POST `/api/v1/work-sessions/{id}/check-out` | IMPLEMENTED (S3-004, PR #9, commit a5a9bcb) — Technician-only, own CHECKED_IN session only, no body, no summary/outcome/evidence (Portfolio Project Owner scope decision); see §4.14 |
| ACC-API-001, ACC-API-002, CST-API-001, CST-API-002 | accept / reject / prepare Cost Summary / review Cost Summary | IMPLEMENTED (Tickets 2, 3, 4a, 4b) — see §4.16, §4.17, §4.18, §4.19 |
| WO-API-010 | POST `/api/v1/work-orders/{id}/close` | IMPLEMENTED (Ticket 5, working tree) — Supervisor-only, no body, full BR-08 guard; see §4.20 |
| CA-*, TIME-*, SLA-*, AUD-*, REP-*, NTF-*, WO-API-011 | — | NOT IMPLEMENTED (later sprints / tickets); `WO-API-008`/`WO-API-009` are superseded by `WSM-API-001`/`WSM-API-002` (§4.15 Decision 3) |

## 2. Implemented Endpoints Missing From the RR-API-001 v1.2 Catalog

| Addendum ID | Method | Path | Purpose | Authorization | Concurrency | Decision / Trace |
|---|---|---|---|---|---|---|
| AUTH-API-001 | POST | `/api/v1/auth/login` | Login | Anonymous | — | DEC-PS1-004 |
| AUTH-API-002 | POST | `/api/v1/auth/refresh` | Rotate refresh token, issue access token | Anonymous (refresh cookie) | — | DEC-PS1-004 |
| AUTH-API-003 | POST | `/api/v1/auth/revoke` | Revoke refresh token / logout | Anonymous (refresh cookie) | — | DEC-PS1-004 |
| CUST-API-001 | GET | `/api/v1/customers` | List Customers (paged) | MasterData.Read | — | DEC-PS1-001 |
| CUST-API-002 | GET | `/api/v1/customers/{customerId}` | Customer detail | MasterData.Read | ETag | DEC-PS1-001 |
| CUST-API-003 | POST | `/api/v1/customers` | Create Customer | MasterData.Manage | — | DEC-PS1-001/015 |
| CUST-API-004 | PATCH | `/api/v1/customers/{customerId}` | Update code | MasterData.Manage | If-Match | DEC-PS1-015; DEC-S1-004-D3 |
| CUST-API-005 | POST | `/api/v1/customers/{customerId}/activate` | Activate | MasterData.Manage | If-Match | DEC-S1-004-D1 |
| CUST-API-006 | POST | `/api/v1/customers/{customerId}/deactivate` | Deactivate (reason) | MasterData.Manage | If-Match | DEC-PS1-013/014; DEC-S1-004-D4 |
| SITE-API-001 | GET | `/api/v1/customers/{customerId}/sites` | List Sites of a Customer (paged) | MasterData.Read | — | DEC-PS1-002 |
| SITE-API-002 | POST | `/api/v1/customers/{customerId}/sites` | Create Site under Customer | MasterData.Manage | — | DEC-PS1-002/015 |
| SITE-API-003 | GET | `/api/v1/sites/{siteId}` | Site detail | MasterData.Read | ETag | DEC-PS1-002 |
| SITE-API-004 | PATCH | `/api/v1/sites/{siteId}` | Update code | MasterData.Manage | If-Match | DEC-PS1-015; DEC-S1-004-D3 |
| SITE-API-005 | POST | `/api/v1/sites/{siteId}/activate` | Activate | MasterData.Manage | If-Match | DEC-S1-004-D1/D2 |
| SITE-API-006 | POST | `/api/v1/sites/{siteId}/deactivate` | Deactivate (reason) | MasterData.Manage | If-Match | DEC-PS1-013/014; DEC-S1-004-D4 |
| EQP-API-001 | GET | `/api/v1/sites/{siteId}/equipment` | List Equipment of a Site (paged) | MasterData.Read | — | DEC-PS1-003 |
| EQP-API-002 | POST | `/api/v1/sites/{siteId}/equipment` | Create Equipment under Site | MasterData.Manage | — | DEC-PS1-003/015 |
| EQP-API-003 | GET | `/api/v1/equipment/{equipmentId}` | Equipment detail | MasterData.Read | ETag | DEC-PS1-003 |
| EQP-API-004 | PATCH | `/api/v1/equipment/{equipmentId}` | Update code | MasterData.Manage | If-Match | DEC-PS1-015; DEC-S1-004-D3 |
| EQP-API-005 | POST | `/api/v1/equipment/{equipmentId}/activate` | Activate | MasterData.Manage | If-Match | DEC-S1-004-D1/D2 |
| EQP-API-006 | POST | `/api/v1/equipment/{equipmentId}/deactivate` | Deactivate (reason) | MasterData.Manage | If-Match | DEC-PS1-003/014 |
| FILE-API-003 | GET | `/api/v1/repair-requests/{id}/attachments` | Attachment metadata list (paged) | RepairRequest.Read | — | DEC-S1-006-F1, DEC-S1-006-PG |
| SYS-API-001 | GET | `/api/health` | Sprint 0 smoke test | Anonymous | — | RR-ARCH-001 §23 |
| SYS-API-002 | GET | `/health` | Health check incl. database | Anonymous | — | RR-ARCH-001 §23 |
| ROUTE-API-001 | GET | `/api/v1/approval-routes` | List approval routes with their single step (paged, `status=ACTIVE\|INACTIVE`) | MasterData.Manage | — | DEC-PRE-S1-007R-04 |
| ROUTE-API-002 | GET | `/api/v1/approval-routes/{approvalRouteId}` | Approval route detail | MasterData.Manage | ETag | DEC-PRE-S1-007R-04 |
| ROUTE-API-003 | POST | `/api/v1/approval-routes` | Create route + single APPROVER step | MasterData.Manage | — | DEC-PRE-S1-007R-03/04/05/06 |
| ROUTE-API-004 | POST | `/api/v1/approval-routes/{approvalRouteId}/activate` | Activate | MasterData.Manage | If-Match | DEC-PRE-S1-007R-06 |
| ROUTE-API-005 | POST | `/api/v1/approval-routes/{approvalRouteId}/deactivate` | Deactivate (no reason; ARC-008 flag) | MasterData.Manage | If-Match | DEC-PRE-S1-007R-04 |
| ROUTING-API-001 | GET | `/api/v1/routing-issues` | SUBMITTED requests not yet routed or with a routing failure (routing metadata only) | Routing.Recovery | — | DEC-PRE-S1-007R-10 |
| ROUTING-API-002 | POST | `/api/v1/repair-requests/{id}/retry-routing` | Admin Retry Routing (server-side route and approver) | Routing.Recovery | If-Match | DEC-PRE-S1-007R-07/08 |
| WO-API-ADD-001 | GET | `/api/v1/work-orders` | List Work Orders (paged; `status` filter; fixed newest-first sort) | WorkOrder.Read | — | DEC-S2-001-01..05 (`docs/12` Section 11) |
| WS-API-ADD-002 | GET | `/api/v1/work-sessions/current` | The caller's own non-CHECKED_OUT Work Session with its pause history, or `204 No Content` when there is none | WorkSession.Read | ETag | S3-002 pre-implementation decision — needed because "My Visits" (WS-API-ADD-001) lists only SCHEDULED visits, so a checked-in technician would otherwise have no way to reach their own session or its `rowVersion`; a technical routing addition, not a new business rule |
| WS-API-ADD-001 | GET | `/api/v1/service-visits/mine` | "My Visits" (UI-040): the caller's own assigned, SCHEDULED Service Visits (paged, fixed soonest-first sort) | MyVisits.Read | — | S3-001 pre-implementation decision — no `docs/09` catalog ID exists for a Service Visit list endpoint at all; this ID is a new technical routing addition, not a new business rule (mirrors how WO-API-ADD-001 formalized the S2-001 list) |

---

## 4.10 S2-001 Work Order List/Detail

Read-only. `WorkOrder.Read` allows REQUESTER, APPROVER, COORDINATOR, TEAM_LEAD, SUPERVISOR — TECHNICIAN and ADMINISTRATOR are denied `403 ACCESS_DENIED` outright (DEC-S2-001-03). Row-level scope mirrors `RepairRequest.Read`/`IDataScope.RepairRequests`, correlated through the Work Order's `repair_request_id` (site-wide roles see Work Orders whose linked Repair Request's Site is in the caller's `user_site_scope`; REQUESTER sees only Work Orders of Repair Requests it created); a nonexistent and an out-of-scope id return the same `404 NOT_FOUND` body (WO-API-001 detail).

Response fields are `workOrderId, workOrderNo, status, repairRequestId, repairRequestNo, customerCode, siteCode, equipmentCode, rowVersion` — reused unchanged for both the list item and the detail response. `customerCode`/`siteCode`/`equipmentCode` are codes, not display names (DEC-S2-001-01). Scheduled Date and Assigned Team/Technician/Team Lead are never returned (DEC-S2-001-02/03); `owner_team_id`/`team_lead_id` are persisted on `work_order` but not projected into the response.

`GET /api/v1/work-orders` query string: `page`, `pageSize` (existing `Paging:DefaultPageSize`/`MaxPageSize` convention) and an optional `status` (one of the `WorkOrderStatus` codes; an unrecognised value is `400 BAD_REQUEST`). Sort is fixed newest-first via a technical `created_at` column added to `work_order` for this purpose only (not one of the RR-DD-001 WO-001..013 business fields, DEC-S2-001-04); there is no client-selectable sort in this ticket.

Convert (`RR-API-010`, `ST-RR-008`/`UC-WO-001` — this sentence previously mislabelled the Close endpoint `WO-API-010`; corrected in §4.20), Schedule, Reassign and every other Work Order mutation are **not implemented** — `work_order` is empty in production until Convert exists. Automated tests seed `RepairRequest`/`WorkOrder` rows directly through EF Core.

### 4.11 S3-001 My Visits / Check-in (PR #6, commit ee115a8)

**WS-API-ADD-001 GET `/api/v1/service-visits/mine` — MyVisits.Read (TECHNICIAN only)**
- Query: `page`, `pageSize` (§3.4 convention; no `status` filter — the list always returns only `SCHEDULED` visits, since that is the only actionable state for Check-in).
- Scope: `IDataScope.AssignedServiceVisits` — every Service Visit whose `assigned_technician_id` is the caller's own id, **and** whose Work Order's Repair Request Site is still in the caller's current `user_site_scope` (a defense-in-depth re-check; the assignment itself was only validated once, at Schedule/Reassign time, and Site scope could have been revoked since). A caller without TECHNICIAN is denied `403 ACCESS_DENIED` by the `MyVisits.Read` policy before any query runs, and the scope query independently returns none for any non-technician — this is not a general Work Order/Visit read scope and does not reuse `WorkOrder.Read`/`IDataScope.WorkOrders`, which deliberately excludes TECHNICIAN (DEC-S2-001-03).
- Sort: fixed soonest-scheduled-first (`scheduled_start_at`, then id) — no client-selectable sort.
- **Response item:** `{ serviceVisitId, workOrderId, workOrderNo, status, siteCode, equipmentCode, scheduledStartAt, scheduledEndAt, rowVersion }` — a lightweight projection, never the full Work Order aggregate graph (`docs/09` §9 "no full aggregate graph for list" principle). `status` is always `SCHEDULED` in practice, returned for shape-consistency with other Service Visit responses.
- Response body: `{ items, page, pageSize, totalCount }` (§3.4).
- "Primary team" plays no part in this scope — no Team/TeamMembership master-data entity exists anywhere in the codebase or docs (confirmed again pre-implementation, same finding as `ServiceVisit.AssignedTeamId`'s own doc comment); eligibility is `assigned_technician_id == caller` only.

**WS-API-001 POST `/api/v1/service-visits/{id}/check-in` — WorkSession.CheckIn (TECHNICIAN only), If-Match, empty body**
- **Client supplies neither status nor Check-in time** — both are server-derived (`docs/09` §1 convention, same as every other action in the codebase). The request body is empty; a body is neither required nor read.
- **Checks, in order:**
  1. Visit not assigned to the caller, out of the caller's tenant, or Site scope no longer current → 404 NOT_FOUND (identical, non-leaking response — same convention as every other Service Visit action).
  2. Stale `If-Match` (checked against the Visit's own `row_version`, not the Work Order's) → 409 CONCURRENCY_CONFLICT.
  3. Visit not `SCHEDULED`, or its Work Order not `SCHEDULED` (ST-WO-002) → 409 STATE_CONFLICT.
  4. Caller already holds a non-`CHECKED_OUT` Work Session on **any** other Visit (BR-05 "no active overlap") → 409 STATE_CONFLICT. A true concurrent race between two Check-in requests for the same technician on two different Visits is additionally backstopped by a DB-level unique filtered index (`IX_work_session_tenant_id_technician_id_active` on `(tenant_id, technician_id) WHERE status <> 'CHECKED_OUT'`), translated to the same 409 CONCURRENCY_CONFLICT if the app-level check alone did not catch it.
- **Success → `200`** with the `WorkOrderResponse` shape (same shape as every other Service Visit action, **except** that `visits` contains only the Visits assigned to the caller — other technicians' Visits and the Coordinator-entered reschedule/reassign/cancel/missed reasons are never returned to a Technician) and a fresh `ETag`. The Work Order and the Visit both move to `IN_PROGRESS`; a new `work_session` row is created, `CHECKED_IN`.
  - **Response scope note:** the success response is fetched via a Technician-scoped read (`WorkOrderService.GetForTechnicianAsync` — "the Work Order has at least one Visit assigned to the caller"), not the Coordinator-oriented `WorkOrder.Read`/`GetAsync` every other Service Visit action's response reuses (which would incorrectly 404 for a Technician caller, since it excludes TECHNICIAN per DEC-S2-001-03). This was found and fixed during S3-001 acceptance testing.
- **Written atomically (one transaction):** `work_order.status` → `IN_PROGRESS`; `service_visit.status` → `IN_PROGRESS`; new `work_session` row (`CHECKED_IN`, `check_in_at` = server clock); two audit rows — `WORK_ORDER_STARTED` (entity Work Order, from `SCHEDULED` to `IN_PROGRESS`) and `SERVICE_VISIT_CHECKED_IN` (entity Service Visit, from `SCHEDULED` to `IN_PROGRESS`, referencing the new `work_session_id`).
- **Not in S3-001:** Pause, Resume, Check-out (`WS-API-002..004`, `ST-WS-002..004`) and location/GPS capture (`docs/01` §2 lists GPS route optimization as explicit Out of Scope; confirmed with the Portfolio Project Owner pre-implementation — no location field exists on `work_session`).
- Trace: ST-WS-001; ST-SV-002; ST-WO-002; BR-05; UC-WO-016 (Check-in half only).

### 4.12 S3-002 Pause Work Session (PR #7, commit db6bc5b)

**Deviations from the RR-* baseline (Portfolio Project Owner directives for S3-002, recorded here rather than silently applied):**
- **"ACTIVE" means `CHECKED_IN`.** The baseline has no ACTIVE status; `CHECKED_IN` is the only state ST-WS-002 allows Pause from, and no status is added.
- **A pause reason is required.** It is not in the baseline: BR-04's reason list does not include Pause and RR-DD-001 WS-001..010 has no reason column. Reason handling here (trimmed, 1–1000 characters, blank or whitespace-only rejected) follows the other BR-04 reason fields.
- **Every pause is its own row.** RR-DD-001 keeps a single `pause_start_at`/`resume_at` pair on `work_session` (WS-007/008), which a second pause would overwrite. A new table `work_session_pause` (`work_session_pause_id`, `tenant_id`, `work_session_id`, `paused_at`, `pause_reason`, `resumed_at`) keeps each pause period so history is never replaced. `work_session.pause_start_at` is still set (WS-007) to the start of the current pause. How Resume resets it is left to that ticket.

**WS-API-002 POST `/api/v1/work-sessions/{id}/pause` — WorkSession.Pause (TECHNICIAN only), If-Match, body `{ "reason": "string" }`**
- **The client supplies only `reason`.** The resulting status and the pause time are server-derived; any other member in the body (e.g. `status`, `pausedAt`) is ignored.
- **Scope (`IDataScope.OwnWorkSessions`):** the session's `technician_id` is the caller, in the caller's tenant, and its Visit is still assigned to the caller within their *current* Site scope. Another technician's session, another tenant's, a revoked Site scope and a nonexistent id all return the same non-leaking `404 NOT_FOUND`.
- **Checks, in order** (the same order as the Service Visit actions):
  1. 400 missing/invalid `If-Match`; 401; 403 ACCESS_DENIED without TECHNICIAN;
  2. 404 NOT_FOUND (scope above);
  3. 409 CONCURRENCY_CONFLICT — stale `If-Match`, checked against the **session's own** `row_version`;
  4. 409 STATE_CONFLICT — session not `CHECKED_IN` (a second Pause of a `PAUSED` session: "This Work Session is already paused.");
  5. 422 VALIDATION_FAILED on `reason` — missing, blank, whitespace-only, or longer than 1000 characters after trimming.
- **Success → `200`** with the `WorkSessionResponse` below and a fresh `ETag` (the session's row version). The Work Order and the Visit stay `IN_PROGRESS` — RR-STS-001 defines no Work Order or Visit transition for Pause.
- **Written atomically (one transaction):** `work_session.status` → `PAUSED` and `pause_start_at`; one new `work_session_pause` row (`paused_at` = server clock, trimmed `pause_reason`, `resumed_at` null); one audit `WORK_SESSION_PAUSED` (entity `WORK_SESSION`, `CHECKED_IN` → `PAUSED`, reason = the pause reason, actor = the technician, `workSessionPauseId` and `pausedAt` in the new value, correlation id). Any failure writes nothing.
- **Concurrency and duplicates:** two concurrent Pause requests with the same `If-Match` → exactly one `200`, the other `409`. A database unique filtered index `IX_work_session_pause_open` on `work_session_pause (work_session_id) WHERE resumed_at IS NULL` allows at most one open pause per session, and `CK_work_session_pause_reason_not_blank` rejects a blank reason, as backstops behind the application checks. BR-05 is unchanged: a `PAUSED` session is still an active session, so the technician cannot Check-in elsewhere.

**WS-API-ADD-002 GET `/api/v1/work-sessions/current` — WorkSession.Read (TECHNICIAN only)**
- Returns the caller's one non-`CHECKED_OUT` session (BR-05 allows at most one) or `204 No Content`; another technician's sessions are never returned. `ETag` = the session's row version.

**WorkSessionResponse:** `{ workSessionId, serviceVisitId, workOrderId, workOrderNo, siteCode, equipmentCode, status (CHECKED_IN | PAUSED | CHECKED_OUT), checkInAt, pauseStartAt, resumeAt, checkOutAt, pauses: [{ workSessionPauseId, pausedAt, pauseReason, resumedAt }] (newest first), rowVersion }`. `docs/09` documents no response body for WS-API-002; this shape is the implementation's.

- **Not in S3-002:** Resume (`WS-API-003`, `ST-WS-003`, implemented S3-003, see §4.13), Check-out (`WS-API-004`, `ST-WS-004`), Work Summary, Time Correction of `PAUSE_START`, and notifications (the baseline matrix defines none for Pause).
- Trace: ST-WS-002; UC-WO-012 (Pause half only); WS-005/007/010; TC-WO-006/007 not yet authored in RR-TC-001.

### 4.13 S3-003 Resume Work Session (PR #8, commit 459501a)

No schema change: `work_session.resume_at` (WS-008) and `work_session_pause.resumed_at` already exist (added by
the S3-001 and S3-002 migrations respectively, both previously unused columns).

**Deviations from the RR-* baseline (Portfolio Project Owner directives for S3-003, recorded here rather than silently applied):**
- **No Business Rule (BR-01..BR-19) is scoped to Resume.** BR-05's "no active overlap" guard traces only to ST-WS-001 (Check-in). Resume introduces no new overlap check: a `PAUSED` session already counts as active for BR-05 (the existing §4.12 decision that `GET /current` treats `PAUSED` as non-`CHECKED_OUT`), so Resume does not reopen that question.
- **`work_session.pause_start_at` (WS-007) is cleared to null on Resume.** The baseline data dictionary is silent on this (§4.12 line "How Resume resets it is left to that ticket"); clearing it reflects that no pause is open any more, matching the "one unmatched pause" description WS-007 already carries. Full pause history is unaffected — it lives entirely in `work_session_pause` and is never rewritten.
- **`work_session.resume_at` (WS-008) holds only the latest Resume's time**, the same "single latest event" column shape as `check_in_at`/`check_out_at`; it is overwritten by a later Resume. The per-pause `resumed_at` on `work_session_pause` is the source of full history.

**WS-API-003 POST `/api/v1/work-sessions/{id}/resume` — WorkSession.Resume (TECHNICIAN only), If-Match, empty body**
- **The client supplies nothing.** Resume has no fields of its own (no reason, unlike Pause); a body is neither required nor read, same convention as Check-in (WS-API-001).
- **Scope (`IDataScope.OwnWorkSessions`, unchanged from S3-002):** the session's `technician_id` is the caller, in the caller's tenant, and its Visit is still assigned to the caller within their *current* Site scope. Another technician's session, another tenant's, a revoked Site scope and a nonexistent id all return the same non-leaking `404 NOT_FOUND`.
- **Checks, in order** (the same order as Pause):
  1. 400 missing/invalid `If-Match`; 401; 403 ACCESS_DENIED without TECHNICIAN;
  2. 404 NOT_FOUND (scope above);
  3. 409 CONCURRENCY_CONFLICT — stale `If-Match`, checked against the **session's own** `row_version`;
  4. 409 STATE_CONFLICT — session not `PAUSED` (a Resume of a `CHECKED_IN` session: "This Work Session is not paused."; a second Resume lands here too, since the session is `CHECKED_IN` again after the first);
  5. defensive 409 STATE_CONFLICT if a `PAUSED` session is somehow found with no open pause row — unreachable in practice (Pause and Resume are the session's only writers) but handled rather than left to crash.
- **Success → `200`** with the `WorkSessionResponse` below and a fresh `ETag` (the session's row version). The Work Order and the Visit stay `IN_PROGRESS` — RR-STS-001 defines no Work Order or Visit transition for Resume, same as Pause.
- **Written atomically (one transaction):** `work_session.status` → `CHECKED_IN`, `resume_at` = server clock, `pause_start_at` cleared; the open `work_session_pause` row's `resumed_at` = the same server clock (its `paused_at`/`pause_reason` are never touched); one audit `WORK_SESSION_RESUMED` (entity `WORK_SESSION`, `PAUSED` → `CHECKED_IN`, no reason, actor = the technician, `workSessionPauseId` and `resumedAt` in the new value, correlation id). Any failure writes nothing.
- **Concurrency and duplicates:** two concurrent Resume requests with the same `If-Match` → exactly one `200`, the other `409`. Unlike Pause, Resume is an UPDATE of the existing session and pause rows, not an INSERT, so the session's own RowVersion compare-and-swap alone is sufficient — no new unique index is needed (`IX_work_session_pause_open` already exists from S3-002 and continues to allow the *next* Pause once this Resume closes the current period).
- **Multiple Pause/Resume cycles:** each Pause after a Resume creates its own new `work_session_pause` row (S3-002's "every pause is its own row" design already supports this); earlier periods' `paused_at`/`pause_reason`/`resumed_at` are never modified by a later cycle.

**WorkSessionResponse** is unchanged from §4.12 — `resumeAt` (top-level, latest Resume only) and each pause period's own `resumedAt` are now both populated by a real Resume instead of always being null.

- **Not in S3-003:** Check-out (`WS-API-004`, `ST-WS-004`, implemented S3-004, see §4.14), Work Summary, Time Correction of `RESUME`, and notifications (the baseline matrix defines none for Resume).
- Trace: ST-WS-003; UC-WO-012 (Resume half); WS-005/007/008/010.

### 4.14 S3-004 Check-out Work Session (PR #9, commit a5a9bcb)

No schema change: `work_session.check_out_at` (WS-009) and `service_visit.completed_at` (SV-014) already exist
(added by the S3-001 migrations, both previously unused columns); `ServiceVisitStatus.Completed` already existed
in the enum, unreachable until now.

**Resolved scope conflict, confirmed with the Portfolio Project Owner before implementation (recorded here rather than silently applied):**
- **BR-06 ties Check-out's own guard directly to Work Summary data** — *"Check-out / Assigned Technician —
  Summary+Outcome + required CLEAN evidence; valid time sequence — Session/Visit completed; audit"*. ST-WS-004's
  guard is *"No active pause; required work facts complete"*; ST-SV-003's guard is *"Summary/outcome/evidence +
  time sequence valid"*; UC-WO-016 states *"Check-out validates summary/outcome/evidence and closes
  session/visit"* — all four baseline sources tie summary/outcome/evidence to the same Check-out action, not to
  a separable later step.
- **`work_summary`/evidence are their own tables** (FK'd to `work_order_id`/`service_visit_id`, not
  `work_session_id`), consumed by a separate, later use case (UC-WO-020, Team Lead/Supervisor, `WO-API-008`)
  whose own precondition assumes the data already exists. Nothing in the baseline sanctions splitting "flip the
  status" from "capture the summary" into two tickets — that split is not a baseline decision.
- **Decision: S3-004 implements Check-out as a pure status transition.** `WorkSession.CheckOut`/`ServiceVisit.CheckOut`
  enforce only their state guards (`CHECKED_IN`→`CHECKED_OUT` and `IN_PROGRESS`→`COMPLETED` respectively); no
  summary/outcome/evidence is collected, validated, or required by this endpoint. BR-06's summary/outcome/evidence
  half remains open for the Work Summary ticket.

**WS-API-004 POST `/api/v1/work-sessions/{id}/check-out` — WorkSession.CheckOut (TECHNICIAN only), If-Match, empty body**
- **The client supplies nothing.** Check-out has no fields of its own (per the resolved scope decision above); a
  body is neither required nor read, same convention as Check-in (WS-API-001) and Resume (WS-API-003).
- **Scope (`IDataScope.OwnWorkSessions`, unchanged from S3-002/S3-003):** the session's `technician_id` is the
  caller, in the caller's tenant, and its Visit is still assigned to the caller within their *current* Site
  scope. Another technician's session, another tenant's, a revoked Site scope and a nonexistent id all return the
  same non-leaking `404 NOT_FOUND`.
- **Checks, in order** (the same order as Pause/Resume):
  1. 400 missing/invalid `If-Match`; 401; 403 ACCESS_DENIED without TECHNICIAN;
  2. 404 NOT_FOUND (scope above);
  3. 409 CONCURRENCY_CONFLICT — stale `If-Match`, checked against the **session's own** `row_version`;
  4. 409 STATE_CONFLICT — session not `CHECKED_IN` (still `PAUSED`, or a second Check-out of an already
     `CHECKED_OUT` session: "This Work Session is already checked out.").
- **Success → `200`** with the `WorkSessionResponse` (unchanged shape from §4.12/§4.13) and a fresh `ETag` (the
  session's row version). The Visit moves to `COMPLETED` (ST-SV-003) in the same call; the Work Order is **never**
  touched (UC-WO-016 postcondition: *"WO remains IN_PROGRESS until summary submit"*).
- **Written atomically (one transaction):** `work_session.status` → `CHECKED_OUT` and `check_out_at`;
  `service_visit.status` → `COMPLETED` and `completed_at`; two audit rows — `WORK_SESSION_CHECKED_OUT` (entity
  `WORK_SESSION`, `CHECKED_IN` → `CHECKED_OUT`, no reason) and `SERVICE_VISIT_COMPLETED` (entity `SERVICE_VISIT`,
  `IN_PROGRESS` → `COMPLETED`, no reason, referencing the closing `workSessionId` in the new value). Any failure
  writes nothing.
- **Concurrency and duplicates:** two concurrent Check-out requests with the same `If-Match` → exactly one `200`,
  the other `409`. Like Resume, Check-out is an UPDATE of the existing session and Visit rows, not an INSERT, so
  the session's own RowVersion compare-and-swap is sufficient — the Visit carries no client token of its own and
  relies on EF's ordinary optimistic check on its own `row_version` (the same treatment Check-in gives the parent
  Work Order).
- **Frees the technician for BR-05:** a `CHECKED_OUT` session is no longer active, so the technician can
  Check-in elsewhere immediately afterward (`GET /current` also returns `204` once checked out).

- **Not in S3-004:** Work Summary/Outcome/Evidence (BR-06's other half — a later ticket), Acceptance, Sprint 4,
  and notifications (the baseline matrix defines none for Check-out beyond BR-06).
- Trace: ST-WS-004; ST-SV-003; UC-WO-016 (Check-out half); WS-009; SV-014.

### 4.15 Sprint 4 Round — Work Summary Submit/Review, Customer Accept/Reject, Work Order Close (decisions recorded ahead of implementation; since implemented — Work Summary as Ticket 1, PR #10, commit cfa09c7; Accept, Reject and Close as Tickets 2, 3 and 5, see §4.16, §4.17, §4.20)

**Context.** UC-WO-020, UC-WO-021, UC-WO-022, and ST-WO-006 (Close) are the next items planned for implementation. No baseline document assigns a sprint number to any of these — see the process note at the end of this section. The decisions below are Portfolio Project Owner directives that deviate from the literal baseline text, recorded here per this addendum's established pattern (§4.12/§4.13/§4.14), **before** any source code, migration, branch commit, or PR — implementation itself is a separate, not-yet-approved step.

**Decision 1 — UC-WO-020 traceability correction.**
UC-WO-020's own Traceability field cites "BR-06/07" (`docs/04`, p.9/14). BR-07's printed text (`docs/02`, Locked Business Rules) is *"Accept/Reject / designated Contact — WO AWAITING_CUSTOMER_ACCEPTANCE; contact active/same Site — Accept -> COMPLETED; Reject -> Corrective Action + reason"* — this is the Customer Accept/Reject rule, which belongs to UC-WO-021/UC-WO-022, not to Work Summary submission. **UC-WO-020 traces to BR-06 only.** BR-07 remains attributed to UC-WO-021/UC-WO-022 (below).

**Decision 2 — Role split for Work Summary submit/review.**
`docs/04` states UC-WO-020's Main Flow as *"Team Lead submits; Supervisor reviews and submits for acceptance"* and `docs/03` ST-WO-003's printed Actor is "Team Lead". Per Portfolio Project Owner directive, this is corrected:
- **Technician** submits Work Summary (ST-WO-003 actor: Technician, not Team Lead).
- **Team Lead or Supervisor** (either one, single step) reviews and submits for acceptance (ST-WO-004 actor: Team Lead OR Supervisor, not Supervisor-only).
- **Customer Acceptance Contact** (WO-008/WO-009, per BR-07/ST-WO-005/ST-WO-007) remains the sole Accept/Reject actor. Team Lead and Supervisor are explicitly barred from Accept/Reject on the customer's behalf — the endpoint pair enforces `AcceptanceContactId` match only, never a Team Lead/Supervisor role check as an alternate path.

**Decision 3 — New technical API IDs (`WSM-API-001`/`WSM-API-002`); `WO-API-008`/`WO-API-009` not reused.**
Because ST-WO-003/004's actors are corrected per Decision 2, `WO-API-008` (`.../submit-work-summary`, baseline actor "Team Lead") and `WO-API-009` (`.../submit-for-acceptance`, baseline actor "Supervisor") no longer describe the actor set that will be implemented. Per Portfolio Project Owner directive, these baseline IDs are **not reused** — they remain in `docs/09`'s catalog as documented but superseded here by two new, addendum-only technical IDs:
- `WSM-API-001` — planned `POST /api/v1/work-orders/{id}/submit-work-summary` — Actor: Technician — If-Match — body: `summaryText`, `repairOutcomeCode`, evidence references.
- `WSM-API-002` — planned `POST /api/v1/work-orders/{id}/submit-for-acceptance` — Actor: Team Lead or Supervisor — If-Match — no body.

`ACC-API-001` (Accept) and `ACC-API-002` (Reject) keep their baseline IDs and actor (Customer Acceptance Contact) unchanged — no deviation on these two. `WO-API-010` (Close) keeps its baseline ID and actor (Supervisor) unchanged — only its guard is trimmed (Decision 4 below), the same category of deviation as §4.14's Check-out (baseline ID kept, guard text corrected here).

**Decision 4 — Work Order Close guard deviation (Cost Summary deferred).**
BR-08 (not BR-12 — corrected per `docs/13` §4.18's own verification against `docs/02`; BR-12 is the unrelated SLA Start/Stop rule) and ST-WO-006's printed guard is *"Work Summary + Cost Summary reviewed"*. Cost Summary (CST-*, CST-API-*) is defined in schema/baseline but implemented in a separately scoped ticket — see §4.18. Per Portfolio Project Owner directive, **Close's guard is trimmed to "Work Summary reviewed" only** for this round; the Cost Summary half of BR-08's guard is deferred to a later ticket — mirroring §4.14's BR-06 Check-out split exactly (baseline ties two things together, one deferred, the deferral documented here rather than silently dropped). **When the Work Order Close ticket is implemented, its guard must check Cost Summary's real `reviewed_at` (per §4.18) — never reduced to just Work Order status COMPLETED.**

> **Superseded by §4.20 (Ticket 5).** The Cost Summary deferral in this Decision is retired now that Cost Summary Prepare/Review exist (§4.18, §4.19): Work Order Close enforces the full `BR-08`/`ST-WO-006` guard, including the real `cost_summary.reviewed_at`. This Decision's text is kept as the record of the earlier round.

**Decision 5 — `repairOutcomeCode` closed allowlist (WSM-007).** RR-DD-001 names this field "Active Outcome" but defines no master-data table or value list anywhere in the baseline (confirmed by exhaustive search across all 13 PDFs, both markdown files, and the full codebase — no `Outcome` lookup entity, table, seed data, or enum exists or ever existed). Per explicit Portfolio Project Owner directive, `repairOutcomeCode` is a **closed six-value allowlist**, required, normalized to uppercase before matching, rejecting anything else (null, empty, whitespace, or an unrecognized code) with `422 VALIDATION_FAILED` — never stored as an arbitrary string:

| Code | Meaning |
|---|---|
| `REPAIRED` | Repair completed successfully and the equipment is back in service. |
| `TEMPORARY_FIX` | A temporary fix was applied; usable but further action is still required. |
| `PARTS_REQUIRED` | Work cannot continue until parts arrive. |
| `NO_FAULT_FOUND` | Investigated but the reported fault could not be found or reproduced. |
| `NOT_REPAIRABLE` | The equipment cannot be repaired. |
| `FOLLOW_UP_REQUIRED` | A further appointment or inspection is required. |

Implemented as a C# enum (`RepairOutcomeCode`, `RepairRequest.Domain.WorkOrders`) with a database `CHECK` constraint generated from the same code list (`CK_work_summary_repair_outcome_code`, mirroring the `UpperSnakeCaseEnumConverter`/`SqlCheck.In<T>()` convention every other status column in this codebase already uses) — the database and the C# allowlist cannot drift apart. This is deliberately **not** a master-data table: no admin-managed CRUD, no per-tenant seeding, no `HasData` — adding, renaming, or removing a value requires another Portfolio Project Owner directive and a code change, the same posture as `WorkOrderStatus`/`WorkSessionStatus`/`MissedVisitDecisionCode`. A future ticket to make Outcome an administrator-managed lookup (mirroring `RequestCategory`/`RequestPriority`) is explicitly out of scope this round.

**Preconditions for Team Lead/Supervisor review (planned `WSM-API-002`).** Both must hold: (a) the underlying Work Session is `CHECKED_OUT`; (b) WO status is already `AWAITING_SUPERVISOR_REVIEW` (i.e., Technician has already submitted via the planned `WSM-API-001`).

**Data scope.** Team Lead/Supervisor: tenant scope plus the Sites in their own `user_site_scope` — planned reuse of the existing `IDataScope.WorkOrders()` (`DataScope.cs`), unchanged, which already implements exactly this (tenant + site-scope join through the Repair Request, gated by `CurrentUser.HasSiteWideWorkOrderScope`). To be enforced backend-only; an Angular route guard, if added, remains UX convenience only, never the security boundary — same posture as every prior ticket.

**Not in this round:** Cost Summary (BR-08's other half — defined in schema/baseline but implemented in a separately scoped ticket, §4.18); Corrective Action's plan/approval/rework cycle (UC-WO-023/024 — Reject is still planned to create a minimal `CorrectiveAction` row in `DRAFT` status atomically, per ST-WO-007's own documented side effect, but nothing beyond that DRAFT row); Cancel Work Order (UC-WO-026); Time Correction (preserved on its own holding branch `feature/s6-time-correction-pending-scope`, untouched by this round).

**Process note.** As with §4.14, no Sprint 4 roadmap or backlog document exists anywhere in `/docs` (confirmed by exhaustive search across all 13 baseline PDFs and both markdown files). UC-WO-020/021/022 and ST-WO-006 are identified here by their own baseline IDs only — this section does not assert, and no evidence supports, that they constitute "Sprint 4" as a defined scope boundary.

- Trace: ST-WO-003/004/005/006/007; BR-06/07/11/12/15; UC-WO-020/021/022; WO-008/009/010/011/012; WSM-001/003/004/005/006/007; EVD-001/003/004/013; CAC-001..010; ACC-001..009.

### 4.16 Ticket 2 — Customer Accept (ST-WO-005; UC-WO-021)

**Context.** UC-WO-021's own scope is Customer Accept only. Before implementation could begin, four requirement gaps had to be resolved by explicit Portfolio Project Owner directive — none of them guessed — recorded here rather than silently applied.

**Decision 1 — Acceptance Contact role: the existing REQUESTER, not a new "CUSTOMER" role.**
No `CUSTOMER` role exists anywhere in this codebase (`RoleCodes.All` has 7 roles: Requester, Approver, Coordinator, Technician, TeamLead, Supervisor, Administrator). The baseline itself (CAC-004) names the Acceptance Contact's source role as `REQUESTER` or `SITE_CONTACT` only, and `RoleCodes.cs`'s own doc comment already states Site Contact is deliberately not a login role. Per Portfolio Project Owner directive, the Acceptance Contact is drawn exclusively from the existing `REQUESTER` role — no role added, no Change Request needed.

**Decision 2 — Active-user check: deferral upheld, not reopened.**
A prior, formal Portfolio Project Owner decision (`docs/12` §DEC-PRE-S1-007-04, 15 September 2026) deferred every ACTIVE/inactive user-state check system-wide until `REQ-FU-USR-001` is delivered, explicitly naming CAC-009 ("Must be active at acceptance") as outside that scope, and explicitly forbidding an `IsActive`/`UserStatus` column. Per Portfolio Project Owner directive, this deferral stands: Accept validates existence, tenant, Site scope and exact identity only — never account "active" status.

**Decision 3 — `acceptanceContactId` is designated via `WSM-API-002`, confirmed by the caller, never free text.**
`WorkOrder.AcceptanceContactId`/`AcceptanceContactSnapshot` (WO-008/WO-009) already existed as a real FK to `ApplicationUser(tenant_id, id)` since S2-001, dormant until now — reused unchanged, no new column. The Team Lead/Supervisor calling `WSM-API-002` (Submit for Acceptance) must supply `acceptanceContactId` in the body; the backend validates it (existing REQUESTER user, same tenant, Site-scoped to the Work Order's Site — `AcceptanceContactEligibility`, mirroring `TechnicianEligibility`'s exact shape) and builds the snapshot (`displayName`, `email` — `displayName` maps to `ApplicationUser.UserName`, the only human-readable field this codebase's user record has; there is no separate display-name column) entirely server-side. The client never supplies a snapshot. Both fields are set atomically with the ST-WO-004 transition, in the same save as the existing Work Summary review flow (§4.15) — no separate write, no way to change the contact afterward in this ticket's scope (no other mutator exists for either field). The `submitted-for-acceptance` audit row now also references `acceptanceContactId`/`acceptanceContactSnapshot`.

To support the Team Lead/Supervisor picking a valid id (never a free-text guess), a new technical lookup is added:
- `ACC-API-ADD-001` — `GET /api/v1/work-orders/{id}/eligible-acceptance-contacts` — Actor: Team Lead or Supervisor (the same actors as `WSM-API-002`, since this exists purely to support it) — returns every eligible REQUESTER's `userId`, `displayName`, `email` only. Never used as authorization by itself. Returns an empty list (not 404) when the Work Order is out of scope or has no Site. The Repair Request's own `RequestContactId`/`CreatedBy` may be offered as a UI-suggested default (since both are already-validated tenant/Site-scoped users), but the caller must still explicitly confirm — including when exactly one eligible candidate exists — and the backend re-validates regardless of any suggestion.

**Decision 4 — `WorkOrderResponse` gains `acceptanceContactId` (a raw id only).**
So an Angular caller can compare it against their own signed-in user id to decide whether to show the Accept button. Never the contact's name/email — those live only in `AcceptanceContactSnapshot`, never projected into any response. The Angular button is UX convenience only, exactly like every route guard in this codebase; the backend re-derives and re-checks identity on every request regardless of what the button's visibility implies.

**ACC-API-001 Customer Accept (ST-WO-005).**
`POST /api/v1/work-orders/{id}/accept` — Actor: the exact designated Acceptance Contact (a REQUESTER) — If-Match required (the Work Order's own RowVersion, the resource named in the URL) — no request body. Moves `AWAITING_CUSTOMER_ACCEPTANCE` → `COMPLETED`. Scope is **resource-specific** (`caller.UserId == WorkOrder.AcceptanceContactId`), not a role-scoped query — contrast `IDataScope.WorkOrders()`'s REQUESTER branch, which covers Work Orders the caller's own Repair Request created (not necessarily this contact), so a dedicated read path (`WorkOrderService.GetForAcceptanceContactAsync`) is used for the response after a successful Accept, mirroring how Check-in already needed a Technician-specific read (`GetForTechnicianAsync`) for the same reason. A different Requester (even in the same tenant and Site), a wrong role, a revoked Site scope, or a nonexistent Work Order all receive the same non-leaking `404 NOT_FOUND`. RowVersion is a true compare-and-swap (EF `OriginalValue` override, not just an app-level check); the state transition and its audit row commit in the same transaction — stale/concurrent/duplicate Accept all return `409` with no partial write.

**Authorization matrix.**

| Actor | Action | Scope |
|---|---|---|
| Team Lead / Supervisor | `ACC-API-ADD-001` read, `WSM-API-002` designate contact | site-wide (`IDataScope.WorkOrders()`, unchanged) + contact validated via `AcceptanceContactEligibility` |
| Exact designated REQUESTER contact | `ACC-API-001` Accept | resource-specific (`AcceptanceContactId == caller`) + current Site scope re-check |
| Any other REQUESTER | Accept | `404` (not `403` — non-leaking, same convention as every other "wrong specific person" case in this codebase) |
| Non-REQUESTER role | Accept | `403 ACCESS_DENIED` (policy gate) |

**Not in this round:** Customer Reject, Corrective Action, Work Order Close, Cost Summary, Cancel Work Order, invitation/activation for a contact with no account yet (the FK already requires an existing `ApplicationUser`, so this gap is deferred to its own future ticket, not blocking Accept).

- Trace: ST-WO-005; BR-07; UC-WO-021; WO-008/009; CAC-004.

### 4.17 Ticket 3 — Customer Reject (ST-WO-007; UC-WO-022; BR-07/BR-15)

**Context.** UC-WO-022's own scope is Customer Reject: the exact designated Acceptance Contact rejecting a Work Order `AWAITING_CUSTOMER_ACCEPTANCE`, creating a `DRAFT` Corrective Action cycle. Before implementation, the Portfolio Project Owner approved a schema decision this ticket depends on — recorded here rather than silently applied.

**Decision 1 — Schema option (a): the baseline `customer_acceptance`/`corrective_action` tables, as the authoritative decision history.**
Before this ticket, Accept (§4.16, ST-WO-005) wrote no decision-history row at all — only `work_order.acceptance_contact_id`/`status` and a `WORK_ORDER_ACCEPTED` audit row. `docs/05` (Data Dictionary) defines two real tables this codebase never created: `customer_acceptance` (ACC-001/003..007/009) and `corrective_action` (CA-001/003..012). Per Portfolio Project Owner directive, this ticket adds both, exactly as documented, and **also retrofits Accept** to write a `customer_acceptance` `ACCEPT` row in the same transaction as its existing Work Order transition and audit row — Accept's own behavior (status, response, audit action code) is otherwise unchanged. `customer_acceptance` is **append-only**: one row per decision (ACCEPT or REJECT), never updated or deleted, so a rejected-then-corrected Work Order keeps its full round-by-round trace (`docs/07` §3 "WorkOrder 1:N CustomerAcceptance").

**Decision 2 — `TenantId` on both new tables: an established technical convention, not a guessed field.**
Neither ACC-* nor CA-* documents a `tenant_id` field (`docs/05` p.7 confirms the ACC-002/CA-002 numeric slots are simply absent from the baseline text, in both `-layout` and `-table` PDF extraction). Every other aggregate in this codebase carries `TenantId` at the analogous undocumented "-002" slot (RR-002, WO-002, SV-002, NTF-002, and `WorkSummary`'s own `TenantId`, added in the immediately-preceding ticket for the same reason) — an established multi-tenancy convention this ticket repeats, not a new guess.

**Decision 3 — `corrective_action.owner_team_lead_id` (CA-007): nullable-for-now deviation.**
CA-007 is documented required ("Y"), but neither ST-CA-001 (`docs/03`, "Create on Customer Reject", guard "Acceptance decision=REJECT") nor UC-WO-022 assigns a Team Lead at DRAFT-creation time — that assignment is ST-CA-002's own step ("Submit Plan", Team Lead actor), out of this ticket's scope. `WorkOrder.TeamLeadId` (WO-007) is never set anywhere in this codebase (confirmed: no mutator exists), so it cannot supply a certain value either. Per Portfolio Project Owner directive, `owner_team_lead_id` is **nullable in this ticket's schema**, populated by nothing yet — to be set by the future Submit-Plan ticket, mirroring the established "trim a guard, defer, record it" pattern already used for BR-06's Check-out split (§4.14) and Ticket 2's Close guard (§4.15 Decision 4). `plan_text`/`plan_file_asset_id` (CA-008/009, "Y@Before Submit") and `approved_by`/`approved_at`/`corrective_service_visit_id` (CA-010/011/012, conditional) are nullable per their own documented conditionality — no deviation needed there.

**Migration/backfill strategy.** Migration `20260924155348_AddCustomerAcceptanceAndCorrectiveAction` creates both tables (with `UNIQUE(work_order_id, acceptance_round_no)` / `UNIQUE(work_order_id, cycle_no)` per `docs/08` §3, and `UNIQUE(corrective_action.acceptance_id)` per the ER's 1:0..1 cardinality), then backfills exactly one `customer_acceptance` `ACCEPT` row for every Work Order already `COMPLETED` with `acceptance_contact_id` set, using its own `WORK_ORDER_ACCEPTED` audit row for `decided_at` (`acceptance_round_no = 1` always — before this ticket, Accept was the only reachable exit from `AWAITING_CUSTOMER_ACCEPTANCE`, so no Work Order could have more than one round). Every backfilled value is read from data that already exists with certainty; nothing is invented, per the explicit "no fabricated data" instruction this decision was made under.

**ACC-API-002 Customer Reject (ST-WO-007).**
`POST /api/v1/work-orders/{id}/reject` — Actor: the exact designated Acceptance Contact (a REQUESTER) — If-Match required (the Work Order's own RowVersion) — body `{ "decisionReason": "string" }`, required (ACC-007 "Required REJECT"; UC-WO-022 precondition "reason required"). Moves `AWAITING_CUSTOMER_ACCEPTANCE` → `CORRECTIVE_ACTION_REQUIRED`. Scope is the same resource-specific check as Accept (`caller.UserId == WorkOrder.AcceptanceContactId`, plus current Site-scope re-check) — shares `IWorkOrderAcceptanceStore.LoadForAcceptAsync` with Accept rather than a duplicate method, since both actions act on the same resource with the same actor. A different Requester, a wrong role, a revoked Site scope, or a nonexistent Work Order all receive the same non-leaking `404 NOT_FOUND` / `403 ACCESS_DENIED` split as Accept. Check order: 400/401/403 (role gate) → 404 (scope) → 409 CONCURRENCY_CONFLICT (RowVersion compare-and-swap) → 409 STATE_CONFLICT (not `AWAITING_CUSTOMER_ACCEPTANCE`) → 422 VALIDATION_FAILED (`decisionReason` missing/blank/over 1000 characters).

**Written atomically (one transaction):** the Work Order's `status`; a `customer_acceptance` `REJECT` row (`decision_reason`, `decided_at`); a `corrective_action` row (`status = DRAFT`, `acceptance_id` referencing the REJECT row just created); one audit `WORK_ORDER_REJECTED` (from `AWAITING_CUSTOMER_ACCEPTANCE`, to `CORRECTIVE_ACTION_REQUIRED`, reason = the decision reason, referencing the new `customer_acceptance`/`corrective_action` ids). RowVersion is the same true compare-and-swap as Accept; stale/concurrent/duplicate Reject all return `409` with no partial write across any of the four writes.

**Authorization matrix.**

| Actor | Action | Scope |
|---|---|---|
| Exact designated REQUESTER contact | `ACC-API-002` Reject | resource-specific (`AcceptanceContactId == caller`) + current Site scope re-check — same as Accept |
| Any other REQUESTER | Reject | `404` (non-leaking, same convention as Accept) |
| Non-REQUESTER role | Reject | `403 ACCESS_DENIED` (policy gate) |

**Angular.** A Reject action beside Accept on the Work Order detail view, shown under the identical UX gate as Accept (`docs/13` §4.16 Decision 3) — never a separate check. Requires a non-blank reason before submit; disabled while a request is in flight (double-submit prevention, the same `submitting()` guard every other action in this component already uses). On `409`, the current Work Order is reloaded (unlike this component's other actions) — once a Work Order leaves `AWAITING_CUSTOMER_ACCEPTANCE` its ETag can never match again, so retrying against the stale one would only loop.

**Not in this round:** Corrective Action's plan/approve/rework/resubmit/re-accept cycle (ST-CA-002/003 and beyond — only the `DRAFT` row this ticket creates); Work Order Close; Cost Summary; Cancel Work Order; invitation/activation. `TC-WO-010`'s baseline scenario ("Reject/plan/approve/rework/resubmit/reaccept", `docs/06` p.5) is only partially satisfied by this ticket — the plan/approve/rework/resubmit/reaccept portion requires the future Corrective Action ticket to close it out completely.

- Trace: ST-WO-007; ST-CA-001; BR-07/BR-15; UC-WO-022; ACC-001..007/009; CA-001/003..012; TC-WO-009/010 (TC-WO-010 partially).

### 4.18 Ticket 4a — Cost Summary Prepare (CST-API-001; BR-08)

**Context.** Cost Summary is defined in schema/baseline but implemented in a separately scoped ticket — no baseline Use Case, no baseline state machine, and no `BR-CST-*`/`ST-CST-*` family exist anywhere in the source documents (confirmed by exhaustive search: `docs/04`'s own "Use Case Coverage Matrix" and `docs/03`'s own "Object → Allowed states" table both omit Cost Summary entirely). BR-08 is the only Business Rule that ever mentions it, and only as Close's own guard precondition (`docs/02` p.4: *"Close / Supervisor — WO COMPLETED; Work Summary + Cost Summary reviewed — WO CLOSED"*). Ticket split: **4a Prepare** (this ticket), **4b Read/Review** (Supervisor, a future ticket), **5 Work Order Close** (a future ticket, which must check Cost Summary's real `reviewed_at` — see §4.15 Decision 4, corrected above).

**Decision 1 — Actor: Team Lead, corroborated by two independent baseline sources, not guessed.**
`docs/05` CST-007 `prepared_by`: *"Team Lead"* and `docs/09` CST-API-001's own Actor column: *"Team Lead"* agree independently — the first Team-Lead-only policy in this codebase (every prior Team Lead capability was shared with Supervisor).

**Decision 2 — No status enum; `reviewed_at` nullability is the only state signal.**
No baseline state machine exists for Cost Summary. Per Portfolio Project Owner directive, no `Draft`/`Prepared`/`Reviewed` enum is added: a null `ReviewedAt` means prepared-but-unreviewed; non-null means reviewed. Nothing in this ticket's scope ever sets `ReviewedBy`/`ReviewedAt` — that is Ticket 4b's sole responsibility.

**Decision 3 — One row per Work Order, mutated in place (not revisioned like `WorkSummary`).**
CST-003 "unique current summary" and `docs/07`'s ER relationship (`WorkOrder 1:0..1 CostSummary`) both confirm exactly one row; Prepare creates it once, then edits it in place until reviewed.

**Decision 4 — Two-phase If-Match target (a deviation from every prior ticket's "always the Work Order's own RowVersion" convention, justified by Prepare never changing the Work Order's own status).**
Every prior command in this codebase changes the Work Order's own `status`, giving its RowVersion a real reason to advance and serve as a compare-and-swap token. Prepare does not — the Work Order stays COMPLETED throughout. If Prepare's If-Match always targeted the Work Order's RowVersion, that token would never advance across repeated edits, so a client could never detect a lost update. Per this ticket's engineering decision: **first-time Prepare** (no existing row) checks `expectedRowVersion` against the **Work Order's** own RowVersion (the only ETag the caller could have, guarding against the Work Order itself changing unexpectedly); **every later edit** checks it against the **Cost Summary's own** RowVersion (CST-011) instead — the ETag returned by the previous Prepare response. `UNIQUE(cost_summary.work_order_id)` is the DB-level backstop against two concurrent first-time Prepares racing (mapped to `409 CONCURRENCY_CONFLICT` the same way the previous ticket's `customer_acceptance`/`corrective_action` unique-index race was fixed).

**Decision 5 — `WorkOrderResponse` gains two Cost Summary signal fields only, not a read endpoint (approved contract).**
`costSummaryRowVersion` and `costSummaryReviewedAt` are added to the already-existing `GET /work-orders/{id}` response — mirroring exactly how `acceptanceContactId` was added in §4.16 Decision 4 — never the amount/currency/note, which stay genuinely hidden until Ticket 4b's read endpoint. Both fields are nullable, with exactly three possible combinations: no Cost Summary yet → both null; prepared but not reviewed → `costSummaryRowVersion` has a value, `costSummaryReviewedAt` is null; reviewed (Ticket 4b) → both have a value. This lets Angular gate the Prepare/Edit UI to a COMPLETED, not-yet-reviewed Work Order and recover the correct If-Match token across page reloads, without building the "Supervisor review queue/read endpoint" this ticket explicitly excludes. **Consequence, recorded not hidden:** Angular's Edit form cannot pre-fill the current amount/currency/note in this ticket — the Team Lead re-enters every field on every edit, blind to the previously prepared values, until Ticket 4b ships.

**Decision 6 — No itemized cost components; `totalAmount` is the sole, server-validated input.**
`docs/05`/`07`/`08` confirm no line-item/itemized-cost table exists anywhere in the baseline — `total_amount` is a single aggregate `decimal(18,2)` figure. Per the approved decision to use only cost fields with baseline evidence, no new cost-category fields are added. "Server-calculated total" (the approved decision) is satisfied as: the server is the sole authority over the canonical stored value after validation (non-negative, at most 2 decimal places, within `decimal(18,2)` range) — never a raw, unvalidated pass-through of client input, though there are no sub-components to sum given the baseline's own single-aggregate-field design.

**Decision 7 — `currencyCode`: ISO format validated, not an ISO-4217 membership list.**
No ISO-4217 master-data table exists anywhere in this codebase. Format only (exactly 3 letters, normalized to upper case) is validated; building a real currency lookup is out of this ticket's scope.

**Decision 8 — Audit action codes: self-named per this codebase's own established convention, not baseline-literal — distinct events for create vs. edit (approved contract).**
No literal baseline action-code string exists for Prepare in `docs/02`, `docs/06` or `docs/09` — BR-08's own audit text names only Close's audit, and TC-CST-001's "Expected Audit" ("Cost review audit") describes Review, not Prepare. Every existing audit action code in this codebase (`WORK_ORDER_ACCEPTED`, `WORK_ORDER_REJECTED`, `WORK_SUMMARY_SUBMITTED`, etc.) is equally self-named, following a consistent `<ENTITY>_<PAST_TENSE_VERB>` convention that has never been literal baseline text either. Per Portfolio Project Owner directive, two distinct events are used — never one shared name that would make create and edit indistinguishable in the audit trail:
- `COST_SUMMARY_PREPARED` — first-time creation of a Work Order's Cost Summary.
- `COST_SUMMARY_UPDATED` — any later edit of an existing, not-yet-reviewed row.

Both are written in the same transaction as the Cost Summary row they describe, and both reference the Work Order (`entity_type`/`entity_id`, the existing `audit_history` shape every other action in this codebase already uses) and the Cost Summary's own id (`costSummaryId` in `new_value_json`), plus the standard `actor_id`/`occurred_at` columns — no new audit shape was introduced.

**CST-API-001 Prepare.**
`PUT /api/v1/work-orders/{id}/cost-summary` — Actor: Team Lead — If-Match required (two-phase target, Decision 4) — body `{ totalAmount, currencyCode, note? }`. Check order (identical to every other command in this codebase): 400/401/403 (role gate) → 404 (scope — `IDataScope.WorkOrders()` site-wide, the same model Work Summary review uses) → 409 `CONCURRENCY_CONFLICT` → 409 `STATE_CONFLICT` (Work Order not COMPLETED, or Cost Summary already reviewed) → 422 `VALIDATION_FAILED`. Success writes the Cost Summary row and one audit row (`COST_SUMMARY_PREPARED` or `COST_SUMMARY_UPDATED`, per Decision 8) in the same transaction; the Work Order's own status/RowVersion are never touched.

**Not in this round:** Supervisor Review, the Supervisor review queue/read endpoint, any mutation of `reviewed_at`/`reviewed_by`, Work Order Close, the Corrective Action lifecycle, any cost category not in the baseline, invitation/activation, Time Correction.

- Trace: CST-API-001; BR-08; CST-001/003..011; TC-CST-001 (partially — the Prepare portion of the "Close guard" scenario only, not Review or Close itself).

### 4.19 Ticket 4b — Cost Summary Read/Review (CST-API-002; BR-08)

**Context.** CST-API-002 ("Review Cost") is the only other baseline endpoint touching Cost Summary — a bare `POST`, Supervisor actor, If-Match, no documented body (`docs/09` p.4). No baseline Use Case, state machine, second test case, or reviewer-comment field exists for Review either (re-verified this round, same exhaustive-search method as §4.18). Five specific requirement gaps were resolved by explicit Portfolio Project Owner directive — none guessed — recorded here.

**Decision 1 — Separation of Duties (baseline is silent; directed).**
No document (`docs/02`, `docs/04`, `docs/12`) states whether a Cost Summary's preparer may also review it. `docs/12`'s own analogous rule for a different feature, DEC-PRE-S1-008-01 ("a user must not Approve or Reject a Repair Request they created, even holding APPROVER"), is itself labeled *"a portfolio rule added beyond the baseline"* — confirming this category of rule is a directed addition, not something the baseline ever specifies. Per Portfolio Project Owner directive, the same posture is applied here: **the reviewer must not be the Cost Summary's own preparer, even holding both Team Lead and Supervisor roles** — `403 ACCESS_DENIED`, checked as an object-level check (mirrors the established `CommandFailure.AccessDenied` category already used for self-decision), after scope, before concurrency/state. Enforced entirely server-side; the Angular UI has no special client-side hint for it (the backend's own 403 message covers it).

**Decision 2 — Review outcome: Mark as Reviewed only (baseline is silent on any second outcome; directed).**
No Reject/Return-for-correction/Reopen action or Corrective-Action-style state enum exists anywhere in the baseline for Cost Summary (confirmed: no second `CST-API-*` id, no `ST-CST-*` beyond the already-absent family, `TC-CST-001` names only one outcome). Per Portfolio Project Owner directive, Review has exactly one outcome. Once reviewed, the Cost Summary is immutable to Prepare — enforced by the same `existing?.ReviewedAt is not null → 409` guard Ticket 4a already shipped (no new guard needed, verified still intact by this ticket's own tests).

**Decision 3 — No reviewer note/reason field (baseline is silent; directed).**
`docs/05`'s complete Cost Summary field list has no reviewer-comment column (contrast `ACC-007 decision_reason`, which does exist for Accept/Reject). Per Portfolio Project Owner directive, the request body carries only the RowVersion/concurrency token (via If-Match) — no reason, no note. `reviewedBy`/`reviewedAt` are always server-derived; the client can never supply them.

**Decision 4 — Review's own state guard: Work Order must be COMPLETED (baseline states this only indirectly, via Close's guard; directed for Review itself).**
No `ST-CST-*` transition exists to carry a guard for Review directly — BR-08/ST-WO-006/CST-010 only state Cost Summary must already be reviewed *by the time of Close*. Per Portfolio Project Owner directive, Review itself is explicitly guarded to `Status == Completed`, mirroring Prepare's identical guard — when this ticket shipped, no implemented command transitioned a Work Order away from COMPLETED, so the guard was verified by a test that arranges the status directly. **Since Ticket 5 (§4.20), Work Order Close transitions a Work Order away from COMPLETED**; a Review attempted after Close is refused `409 STATE_CONFLICT`, covered by Ticket 5's lock regression test.

**Decision 5 — Supervisor pending-review queue: a technical addition, not a baseline endpoint (approved, recorded here per instruction).**
No document names a "queue" (exhaustive search of `docs/02`, `docs/04`, `docs/06`, `docs/09`). Unlike `ACC-API-ADD-001` (which existed to populate a *required input field* on a different mandatory action), this queue has no comparable baseline-adjacent precedent — it is a new technical addition purely to let a Supervisor discover what needs review at all. `GET /api/v1/work-orders/pending-cost-summary-review` — Supervisor only, site-wide via the existing `IDataScope.WorkOrders()` (unchanged), filtered to `Status = COMPLETED AND CostSummary exists AND ReviewedAt IS NULL`, paged and newest-prepared-first (a deterministic ordering choice mirroring this codebase's established newest-first convention, DEC-S2-001-04 — not baseline-ordered, since no order is documented). A filtered index (`IX_cost_summary_pending_review ON cost_summary(reviewed_at) WHERE reviewed_at IS NULL`) backs this exact predicate.

**CST-API-002 Review, and the new Cost Summary read.**
`POST /api/v1/work-orders/{id}/review-cost-summary` — Actor: Supervisor — If-Match required (the Cost Summary's own RowVersion — Review, like a Prepare edit, never changes the Work Order's own status) — empty body. Check order: 400/401/403 (role gate) → 404 (scope: Work Order in Supervisor's Site scope, and a Cost Summary must already exist — no Cost Summary yet is 404, the same "resource doesn't exist yet" convention as every other command in this codebase) → 403 `ACCESS_DENIED` (Separation of Duties, Decision 1) → 409 `CONCURRENCY_CONFLICT` → 409 `STATE_CONFLICT` (not COMPLETED, or already reviewed). Success sets `reviewed_by`/`reviewed_at`, writes one audit row, all in the same transaction as the Cost Summary row — the Work Order's own status/RowVersion are never touched (mirrors Prepare exactly).

A Cost Summary read, `GET /api/v1/work-orders/{id}/cost-summary` (technical addition, no baseline endpoint — mirrors `WorkSummaryStore.GetWorkSummaryAsync`'s combined scope+read shape), is shared by both actors: Team Lead (their own Prepare scope — closes Ticket 4a's own flagged gap, the Prepare/Edit form can now pre-fill current values) and Supervisor (site-wide, to review). `CostSummary.Read` is its own narrower policy than `WorkOrder.Read` — Requester/Technician/anyone else must never see cost amounts, per the approved decision; `WorkOrderResponse` itself still carries only the two signal fields (`costSummaryRowVersion`, `costSummaryReviewedAt`) added in §4.18, never the amount/currency/note.

**Audit event.** `COST_SUMMARY_REVIEWED` — its own distinct event, following the same established self-named `<ENTITY>_<PAST_TENSE_VERB>` convention as `COST_SUMMARY_PREPARED`/`COST_SUMMARY_UPDATED` (§4.18 Decision 8), flagged the same way. References the Work Order (`entity_type`/`entity_id`) and the Cost Summary's own id (`costSummaryId` in `new_value_json`), plus the standard `actor_id` (the reviewing Supervisor)/`occurred_at` columns — no new audit shape.

**Authorization matrix.**

| Actor | Action | Scope |
|---|---|---|
| Team Lead | `GET .../cost-summary` | their own Prepare scope (`IDataScope.WorkOrders()`) |
| Supervisor | `GET .../cost-summary`, `GET .../pending-cost-summary-review`, `POST .../review-cost-summary` | site-wide (`IDataScope.WorkOrders()`) |
| The Cost Summary's own preparer | Review | `403 ACCESS_DENIED` (Separation of Duties) — even holding both roles |
| Any other Supervisor (wrong tenant/site) | any of the above | `404` (non-leaking, same convention as every other "wrong specific person/scope" case in this codebase) |
| Requester/Technician/Approver/Coordinator/Administrator | any of the above | `403 ACCESS_DENIED` (policy gate) |

**Angular.** Supervisor-only pending-review list (`/cost-summary-review`, gated by a new `supervisorOnlyGuard`, UX convenience only), "View Details" fetches and shows the prepared amount/currency/note/preparer inline, "Review" re-fetches the current Cost Summary first (so If-Match always targets its own fresh RowVersion) then submits — disabled while in flight (double-submit prevention), refreshes the list on a 404/409 (stale/already-reviewed/gone). The Work Order detail page's own Prepare/Edit form (Team Lead) now also fetches the existing Cost Summary on load when one exists, pre-filling the form — Ticket 4a's own flagged gap, closed now that Read exists.

**For the future Work Order Close ticket:** `reviewed_at` becoming non-null here is the exact evidence Close's guard (`docs/13` §4.15 Decision 4, corrected) must check — Close must never be reduced to checking Work Order status `COMPLETED` alone. Review itself never changes the Work Order's own status; only the Close ticket's own `ST-WO-006` transition does (implemented as Ticket 5 — §4.20).

**Not in this round:** Reject/Return-for-correction/Reopen of a Cost Summary, a Review reason/comment field, Work Order Close, the Corrective Action lifecycle, Cancel Work Order, invitation/activation, Time Correction.

- Trace: CST-API-002; BR-08; CST-001/003..011; TC-CST-001 (still only partially satisfied — Review's own behavior has no baseline test case at all).

### 4.20 Ticket 5 — Work Order Close (ST-WO-006; WO-API-010; BR-08)

**Context.** Close is defined by `ST-WO-006` (`docs/03`, Work Order transition matrix: `COMPLETED → CLOSED`, actor Supervisor, guard "Work Summary + Cost Summary reviewed", result "SLA stop; lock operations"), `BR-08` (`docs/02`), `WO-API-010` (`docs/09`: `POST /api/v1/work-orders/{id}/close`, Supervisor, If-Match), `TC-WO-011` and `TC-CST-001` (`docs/06`), and RR-DD-001 `WO-011 closed_by` / `WO-012 closed_at` (`docs/05`). **No Use Case exists for Close** — `docs/04` lists `UC-WO-020..024` and `UC-WO-026` and no `UC-WO-025` appears anywhere in `/docs` (confirmed by exhaustive search of all baseline PDFs and both markdown files); Close is therefore traced by the five IDs above only, and no Use Case ID is invented. "Ticket 5" is the working label from the §4.18 ticket split, not a baseline ID. Decisions Q1–Q7 below were each proposed with the baseline text, the options and a recommendation, and approved by explicit Portfolio Project Owner directive — none guessed.

**Supersession of §4.15 Decision 4.** §4.15 Decision 4 trimmed Close's guard to "Work Summary reviewed" only, deferring the Cost Summary half. That deviation is **retired**: Cost Summary now exists (§4.18 Prepare, §4.19 Review), so Close enforces the **full** `BR-08`/`ST-WO-006` guard, and its Cost Summary half checks the real `cost_summary.reviewed_at` — never Work Order status `COMPLETED` alone. Decision 4's text above is kept unedited as the historical record of the earlier round.

**Q1 — SLA stop is deferred (directed deviation).** `ST-WO-006`/`BR-08` state "SLA stop" as Close's result (also `BR-12`, `EV-SLA-005`, `docs/05` `SLA-011 stopped_at` / `SLA-012 stop_reason = WO_CLOSED`, `docs/08` "→ SLA stop → audit"). No `sla_record` table exists in this codebase: `DEC-PRE-S1-007-12` (`docs/12`) wrote no `sla_record` because `SLA-004`/`SLA-006` belong to the deferred SLA Policy scope, and Request Reject/Cancel deferred the same stop for the same reason (S1-008/S1-009). Per Portfolio Project Owner directive, **Close does not stop an SLA in this round and no SLA schema or logic is added**; `work_order.closed_at` is the future stop marker for whoever implements the SLA scope. `TC-WO-011` asserts no SLA outcome, so no baseline test is affected; `UAT-01`'s "SLA history" remains open until SLA exists.

**Q2 — Close prerequisites (all must hold; any miss is `409 STATE_CONFLICT` with a guard-specific message, nothing written).**

| # | Prerequisite | How it is checked (existing data only — no new schema) |
|---|---|---|
| 1 | Work Order is `COMPLETED` | `work_order.status`; a `CLOSED` Work Order gets the message "already closed" |
| 2 | A Work Summary exists | a `work_summary` row for the Work Order exists (only revision 1 can exist today, since no resubmission cycle is implemented, so any row is the current one) |
| 3 | The Work Summary was reviewed | `work_order.acceptance_contact_id IS NOT NULL` — see below |
| 4 | The customer accepted **the current** submission | the `customer_acceptance` row with the **highest** `acceptance_round_no` for the Work Order has decision `ACCEPT` — never "any `ACCEPT` row in history" |
| 5 | A reviewed Cost Summary exists | a `cost_summary` row exists **and** `reviewed_at IS NOT NULL` (§4.18 Decision 2, §4.19) |

*Why prerequisite 3 uses `acceptance_contact_id` instead of a review flag.* RR-DD-001 defines no review column for the Work Summary (`WSM-001/003..007` only — §4.15 evidence). `WO-008 acceptance_contact_id` ("Required before Acceptance", `docs/05`) is assigned by `WorkOrder.SubmitForAcceptance` (`ST-WO-004`, whose baseline guard is literally "Summary reviewed") in this codebase, so Close uses a non-null value as the evidence that the Work Summary was reviewed and submitted for acceptance. This is an evidence rule, not a proof: it does not cover a value written outside that method (for example directly in the database), and the API test that sets the column to NULL directly in SQL verifies only that Close refuses that state, not whether it can be reached through the public APIs. §4.15 Decision 2 lets a Team Lead **or** a Supervisor perform that single review step, so "reviewed" does not mean a Supervisor personally reviewed it; Close does not add that requirement.

*Why prerequisite 4 is tied to the current cycle.* `customer_acceptance` has no foreign key to `work_summary`; its `acceptance_round_no` is the ordering of decisions (§4.17), and each Reject opens a corrective cycle that must be followed by a later round. The highest round is therefore the decision on the current submission. The transition matrix contains only `ST-WO-001..007` (`WorkOrderStatusTransitionsTests` asserts exactly seven transitions), so the Corrective Action lifecycle (`ST-WO-008..011`) is not implemented and the real flow produces only a first-round `ACCEPT` today; the highest-round rule is the intended reading for when that lifecycle ships and is not exercised through it. What is proven: `WorkOrderCloseEndpointsTests` seeds acceptance rounds directly — an earlier `ACCEPT` followed by a latest `REJECT` is refused (the case a check for *any* `ACCEPT` row would wrongly pass), a Work Order whose only round is a `REJECT`, or that has no round at all, is refused, and rounds `ACCEPT`, `REJECT`, `ACCEPT` (1–3) are accepted.

**Q3 — Audit.** One `WORK_ORDER_CLOSED` row in the existing `audit_history` shape, written in the same transaction as the status change: `entity_type = WORK_ORDER`, `entity_id` = the Work Order, `from_state = COMPLETED`, `to_state = CLOSED`, `actor_id` = the closing Supervisor, `occurred_at`, correlation id, and `costSummaryId` (the reviewed Cost Summary that satisfied the guard) in `new_value_json`. The code follows the self-named `<ENTITY>_<PAST_TENSE_VERB>` convention of §4.18 Decision 8 (the baseline names only "Close audit"). A failed or rejected Close writes no audit row; a duplicate or concurrent Close never produces a second one.

**Q4 — Contract.** `WO-API-010`: `POST /api/v1/work-orders/{id}/close`, Actor: Supervisor, `If-Match` required (the **Work Order's own** RowVersion — Close changes the Work Order's status, so unlike Prepare/Review its own token is the correct compare-and-swap target; once reviewed, the Cost Summary is refused by Prepare and Review with `409` (§4.18, §4.19), so it is not a second concurrency token Close needs). **Empty body**; `closed_by` and `closed_at` are always server-derived. Returns the updated Work Order with a fresh ETag. No reason field exists for Close (`docs/02` BR-05's reason-required list and `docs/05` name none; only Cancel has `cancel_reason`).

**Check order and errors (Q6).** 400 (malformed/missing If-Match) / 401 → `403 ACCESS_DENIED` (no Supervisor role) → `404 NOT_FOUND` (Work Order missing or outside the caller's tenant/Site scope, non-leaking) → `409 CONCURRENCY_CONFLICT` (stale RowVersion) → `409 STATE_CONFLICT` (not `COMPLETED`, or any prerequisite 2–5 unmet — each with its own message). A duplicate or concurrent Close resolves to exactly one success and `409` for the rest (a second Close sees either the new RowVersion or status `CLOSED`).

**State transition and data scope.** `ST-WO-006`: `COMPLETED → CLOSED`, sets `closed_by`, `closed_at`. Data scope is the existing `IDataScope.WorkOrders()` unchanged (tenant + the Sites in the caller's own `user_site_scope`, through the Work Order's Repair Request), gated first by the new `WorkOrder.Close` policy (`SUPERVISOR` only). Enforced backend-only; Angular gating is UX convenience only.

**Q5 — No extra guards; lock after Close.** The baseline and this addendum define none, so Ticket 5 adds none: no Separation of Duties for Close (preparer ≠ reviewer already exists for the Cost Summary, §4.19; any in-scope Supervisor may Close once the prerequisites hold, including one who reviewed the Cost Summary), and no Service Visit, Work Session or Corrective Action guard — `ST-WO-006`, `BR-08` and `WO-API-010` state no such condition, and this ticket does not add a business rule the baseline does not have. This section makes no claim about whether a non-final Service Visit, an open Work Session or a Corrective Action can coexist with a `COMPLETED` Work Order; that is neither asserted nor tested here. "Lock operations" (`ST-WO-006`) is satisfied by the existing state guards and **proven by a regression test**, `AfterClose_EveryMutationCommand_IsRejected_AndNothingChanges` (`WorkOrderCloseEndpointsTests`): after Close, each of 16 mutation commands — a second Close; Cost Summary Prepare and Review; Customer Accept and Reject; Submit Work Summary; Submit for Acceptance; Work Order Schedule; Service Visit Check-in, Reschedule, Reassign, Cancel and Mark Missed; Work Session Pause, Resume and Check-out — returns `409 STATE_CONFLICT` (the test requires that exact code, so a stale-token `CONCURRENCY_CONFLICT` cannot mask a missing state guard) and changes nothing. The Decide-Missed command is not covered by that test. The Work Order never leaves `CLOSED`: there is no Reopen in MVP (`docs/01`, `docs/03` §7) and `WorkOrderStatusTransitionsTests` asserts that no transition leaves `CLOSED`.

**Q7 — Response.** `WorkOrderResponse` gains `closedAt` (additive, nullable; the moment of Close) so users can see when the work was closed; `closedBy` is **not** exposed — it is an internal user id, kept only in `work_order.closed_by` and in the `WORK_ORDER_CLOSED` audit row's `actor_id`, so it is never shown to a Requester. No migration: `closed_by` (with its `FK_work_order_closed_by_user`) and `closed_at` already exist on `work_order`.

**Authorization matrix.**

| Actor | Action | Scope |
|---|---|---|
| Supervisor | `WO-API-010` Close | `IDataScope.WorkOrders()` (tenant + own Site scope) |
| Supervisor of another tenant / without Site scope on the Work Order | Close | `404` (non-leaking) |
| Requester / Approver / Coordinator / Technician / Team Lead / Administrator (without Supervisor) | Close | `403 ACCESS_DENIED` (policy gate) |

**Angular.** The Work Order detail page shows a "Close Work Order" button only when status is `COMPLETED`, `costSummaryReviewedAt` is non-null and the signed-in user holds `SUPERVISOR` (UX only); it is disabled while the request is in flight (double-submit prevention), sends the Work Order's `rowVersion` as If-Match, shows the server's message on `409` and reloads the Work Order, and shows `closedAt` and hides the Prepare/Close controls once `CLOSED`.

**Not in this round:** SLA stop and `sla_record` (Q1); Cancel Work Order (`WO-API-011`, `UC-WO-026`); Reopen; the Corrective Action plan/approve/rework/resubmit/re-accept lifecycle (`ST-WO-008..011`, `UC-WO-023/024`); a Close notification or outbox event (none is defined in `docs/01`'s notification matrix, and `docs/08` lists only "audit" for Close); any change to Cost Summary behaviour; invitation/activation; Time Correction.

- Trace: ST-WO-006; BR-08 (BR-12/`EV-SLA-005` deferred, Q1); WO-API-010; WO-011/WO-012; TC-WO-011; TC-CST-001 (Close portion now satisfied end to end).

### 4.21 Ticket 6 — Corrective Action: Submit Plan / Approve Plan (CA-API-001; CA-API-002; docs/02 Definitions "Corrective Action")

**Context.** `docs/02`'s own Definitions section states the full cycle: *"Corrective Action: Created automatically from Customer Reject; Team Lead plan → Supervisor approval → rework → review → same contact re-acceptance."* Ticket 3 (§4.17) already implements the first step — the automatic `DRAFT` row created by Reject (`ST-CA-001`). Ticket 6 implements the next two steps only — **Submit Plan** (`CA-API-001`, `docs/09`: `POST /api/v1/corrective-actions/{id}/submit-plan`, Team Lead, If-Match) and **Approve Plan** (`CA-API-002`, `docs/09`: `POST /api/v1/corrective-actions/{id}/approve-plan`, Supervisor, If-Match) — plus the minimum read model needed to reach them from the Work Order detail page. Rework/resubmission/re-acceptance (`CA-API-003`, `ST-WO-010`, `UC-WO-024`) is explicitly deferred to a future ticket. "Ticket 6" and "Sprint 4" (as the name of this round) are Portfolio Project Owner labels, not baseline IDs — the same posture already recorded for the prior round at §4.15's own process note.

The Project Owner decisions below (a)–(e) were each proposed with the baseline text, the options and a recommendation, and approved by explicit directive — none guessed.

**Decision (a) — `owner_team_lead_id` bound atomically at Submit Plan; `WorkOrder.TeamLeadId` untouched.**
`docs/05` marks `CA-007 owner_team_lead_id` as bare "Y" (contrast `CA-008 plan_text`/`CA-009 plan_file_asset_id`, both "Y@Before Submit" — the Data Dictionary's own notation, confirmed by the same pattern on `WO-008 acceptance_contact_id: Y@Before Acceptance"), which read literally would require it non-null from `DRAFT` creation (`ST-CA-001`, Ticket 3). Ticket 3 (§4.17) already recorded a directed deviation leaving it nullable, reasoning that no baseline step assigns a Team Lead at Reject time. `WorkOrder.TeamLeadId` (`WO-007`, "Active Team Lead") exists on the Work Order since S2-001 but has no mutator anywhere in this codebase (`WorkOrder.Schedule` sets only `OwnerTeamId`); sourcing `CA-007` from it would copy null forward, and giving it a first mutator would be a separate, larger change to the already-shipped S2-003 Schedule feature. **Per Portfolio Project Owner directive: `owner_team_lead_id` may stay null while `DRAFT`; Submit Plan binds it, atomically with the `DRAFT→PENDING_PLAN_APPROVAL` transition, to the authorized submitting Team Lead — self-assignment on first (and only) use, the same precedent already established for `AcceptanceContactId`/`AcceptanceContactSnapshot` (§4.16 Decision 3).** `WorkOrder.TeamLeadId` is explicitly not touched by this ticket. No unaudited owner change is possible: `CorrectiveActionStatus` is a closed three-value set with no path back to `Draft`, so Submit Plan's own state guard (`Status == Draft`) makes a second assignment unreachable, and the one `CORRECTIVE_ACTION_PLAN_SUBMITTED` audit row already records the actor.

**Decision (b) — Minimum read model: `correctiveActionId`/`correctiveActionStatus` on `WorkOrderResponse`, derived live, never stored twice.**
No `GET /corrective-actions/{id}` (or any Corrective Action read endpoint) exists anywhere in `docs/09`'s catalog, and before this ticket `WorkOrderResponse`/`WorkOrderDto` expose nothing about a Corrective Action — so a Team Lead viewing a `CORRECTIVE_ACTION_REQUIRED` Work Order has no way to obtain the `{id}` `CA-API-001` needs. Per Portfolio Project Owner directive, **two additive, nullable signal fields — `correctiveActionId` and `correctiveActionStatus` — are added to `WorkOrderResponse`/`WorkOrderDto`**, mirroring exactly how §4.18 Decision 5 added `costSummaryRowVersion`/`costSummaryReviewedAt` to the same response: a technical addition, not a new business rule, never a dedicated Corrective Action read endpoint, and never `plan_text`/`plan_file_asset_id`/`approved_by`/`approved_at` (those stay genuinely hidden, mirroring the amount/currency/note precedent). Both fields are **derived live** by the existing `WorkOrderStore.Project` join (the same query shape already used for `costSummaryRowVersion`) — no duplicate/cached copy of Corrective Action state is stored anywhere else. Existing site scope (`IDataScope.WorkOrders()`) is unchanged and unconditionally applies to this projection, the same as every other field on this response. This is not the pending-review queue the prior round declined to add (§4.19 Decision 5's own precedent for *why* a queue is a bigger technical addition than a signal field) — it exposes only the one Work Order already being viewed, one id and one status code.

**Decision (c) — `CA-API-003` (Start Rework) actor deferred; not decided or implemented in Ticket 6.**
`docs/09` prints `CA-API-003`'s actor ambiguously as *"Coordinator/Supervisor authorized"*; `docs/10` UI-060 lists *"Team Lead/Supervisor/Coordinator — Plan/approve/schedule rework"* (a second, independent source, pairing each role with one verb in order); `docs/03` gives `ST-WO-010` (the corrective Visit's own Check-in, `CORRECTIVE_PLAN_APPROVED→IN_PROGRESS`) to the Assigned Technician — a separate action from creating that Visit. Per Portfolio Project Owner directive, **this actor decision is deferred to `CA-API-003`'s own future ticket; Ticket 6 does not implement or decide Start Rework.** (For that future ticket's own use: the evidence above points at Coordinator for `CA-API-003` specifically — the same actor as every other Visit-creation action in this codebase, `WO-API-002`/`ST-SV-001` — distinct from Supervisor's plan-approval duty and the Technician's later check-in; not decided here, recorded only so it is available when that ticket is scoped.)

**Decision (d) — No Separation-of-Duties guard for Approve Plan; recorded as a review risk, not a baseline requirement.**
No document (`docs/02`, `docs/04`, `docs/09`, `docs/12`) states whether a Supervisor may approve a plan submitted by themselves (relevant only to a user holding both Team Lead and Supervisor roles). Per Portfolio Project Owner directive, **no additional guard is added — Approve Plan enforces only the baseline Supervisor role, `IDataScope.WorkOrders()` site scope, the state guard (`PENDING_PLAN_APPROVAL` only) and RowVersion concurrency.** This is explicitly recorded as a **review risk**, not a claim that the baseline requires or forbids a same-user guard: unlike Cost Summary Review (§4.19 Decision 1), where a directed Separation-of-Duties rule was approved, no equivalent directive was given here. A future ticket may revisit this.

**Decision (e) — "V1 ends at Service Report + Cost Summary" is a delivery-increment boundary, not baseline text, and does not remove Ticket 3's mechanism.**
No baseline document uses the terms "V1" or "Service Report" (confirmed by exhaustive search across all 13 PDFs and both markdown files, the same method as every prior "no evidence" claim in this addendum). This statement is a Portfolio Project Owner scope/change-control decision about this delivery's own boundary, layered on top of the baseline — recorded here, not attributed to `/docs`. It does not retroactively exclude Ticket 3's already-merged, Reject-triggered `DRAFT` Corrective Action row from being completed by a later ticket; Ticket 6 continues that mechanism, it does not reopen or reinterpret it.

**CA-API-001 Submit Plan.**
`POST /api/v1/corrective-actions/{id}/submit-plan` — Actor: Team Lead — If-Match required (the **Work Order's own** RowVersion, not a new `corrective_action` token — see the concurrency note below) — body `{ planText, planFileAssetId? }` (CA-008/CA-009, "Y@Before Submit"). Check order (identical convention to every other command in this codebase): 400/401/403 (role gate) → 404 (scope: the linked Work Order in `IDataScope.WorkOrders()`, and the Corrective Action id must belong to that Work Order in the caller's tenant — non-leaking) → 409 `CONCURRENCY_CONFLICT` (stale Work Order RowVersion) → 409 `STATE_CONFLICT` (Corrective Action not `DRAFT`) → 422 `VALIDATION_FAILED` (blank/oversized `planText`; `planFileAssetId` missing, belonging to another tenant, or not yet `CLEAN` — see the CLEAN-evidence note below). Success, one transaction: `owner_team_lead_id` = caller (Decision a), `plan_text`/`plan_file_asset_id` set, `CorrectiveActionStatus: DRAFT→PENDING_PLAN_APPROVAL`, `WorkOrder: CORRECTIVE_ACTION_REQUIRED→CORRECTIVE_PLAN_PENDING` (`ST-WO-008`), one `CORRECTIVE_ACTION_PLAN_SUBMITTED` audit row.

**`planFileAssetId` must be CLEAN — literal baseline text, not a directed decision.** `UC-WO-023`'s own Preconditions state *"Plan text + CLEAN evidence"* (`docs/04`), and its Validation/Authorization line names *"file scan"* explicitly — this was missed in the first pass of this section and is corrected here, not decided. Submit Plan checks `file_asset.tenant_id` matches and `file_asset.malware_scan_status = CLEAN` in one query (`ICorrectiveActionStore.IsPlanFileUsableAsync`), the same single EXISTS-style shape already used for the Repair Request's own CLEAN-photo Submit gate (`IRepairRequestSubmitStore.HasCleanPhotoAsync`, DEC-PRE-S1-007-06). A missing file, a file from another tenant, and a file that is still `PENDING` or is `FAILED` (infected) are all reported as the same `422 VALIDATION_FAILED` on `planFileAssetId` — mirroring how the Repair Request's own CLEAN-photo gate reports one generic message rather than distinguishing the reason, since telling an unauthorized caller *why* a file id they guessed doesn't work would leak information the non-leaking scope check upstream is designed to withhold.

**CA-API-002 Approve Plan.**
`POST /api/v1/corrective-actions/{id}/approve-plan` — Actor: Supervisor — If-Match required (the Work Order's own RowVersion) — empty body. Check order: 400/401/403 → 404 (same scope shape as Submit Plan) → 409 `CONCURRENCY_CONFLICT` → 409 `STATE_CONFLICT` (Corrective Action not `PENDING_PLAN_APPROVAL`). Success, one transaction: `approved_by`/`approved_at` set, `CorrectiveActionStatus: PENDING_PLAN_APPROVAL→APPROVED`, `WorkOrder: CORRECTIVE_PLAN_PENDING→CORRECTIVE_PLAN_APPROVED` (`ST-WO-009`), one `CORRECTIVE_ACTION_PLAN_APPROVED` audit row.

**Why the Work Order's own RowVersion, not a new `corrective_action` token.** `docs/05`'s Corrective Action field list stops at `CA-012` — no `CA-013 row_version` is documented, and neither the current EF configuration nor the original migration (`20260924155348_AddCustomerAcceptanceAndCorrectiveAction`) defines one. Unlike Cost Summary Prepare/Review (§4.18 Decision 4, which never change the Work Order's own status and so needed a two-phase If-Match), **both Submit Plan and Approve Plan transition the Work Order's own status** (`ST-WO-008`/`ST-WO-009`), giving its RowVersion a real, advancing reason to serve as the compare-and-swap token — the same pattern already used by Accept/Reject/Close/Schedule. No migration is needed for concurrency on `corrective_action`.

**Audit.** `CORRECTIVE_ACTION_PLAN_SUBMITTED` and `CORRECTIVE_ACTION_PLAN_APPROVED` — the first two audit rows in this codebase whose `entity_type` is `CORRECTIVE_ACTION` rather than `WORK_ORDER`/`SERVICE_VISIT`/`WORK_SESSION` (mirroring how Schedule's own audit uses the Work Order as `entity_type` while referencing the created Visit only in `new_value_json` — here the roles are reversed: the Corrective Action is the entity that changed, the Work Order id is referenced in `new_value_json` since its own status changed too). Self-named per the established `<ENTITY>_<PAST_TENSE_VERB>` convention (no literal baseline action-code string exists for either step).

**Authorization matrix.**

| Actor | Action | Scope |
|---|---|---|
| Team Lead | `CA-API-001` Submit Plan | `IDataScope.WorkOrders()` (site-wide) via the linked Work Order — nothing to scope against `owner_team_lead_id` yet, mirroring Cost Summary's first-time Prepare |
| Supervisor | `CA-API-002` Approve Plan | `IDataScope.WorkOrders()` (site-wide); no Separation of Duties (Decision d) |
| Team Lead/Supervisor of another tenant or without Site scope | either | `404` (non-leaking) |
| Requester/Approver/Coordinator/Technician/Administrator | either | `403 ACCESS_DENIED` |

**Angular.** The Work Order detail page gains, gated purely on `correctiveActionId`/`correctiveActionStatus` (UX only, backend re-checks regardless): a "Submit Corrective Plan" form (Team Lead, shown when `correctiveActionStatus == 'DRAFT'`) and an "Approve Corrective Plan" button (Supervisor, shown when `correctiveActionStatus == 'PENDING_PLAN_APPROVAL'`) — both send the Work Order's own `rowVersion` as If-Match, disable while in flight, and on `409` reload the current Work Order.

**Not in this round:** `CA-API-003` (Start Rework) and its actor (Decision c); `ST-WO-010` (Technician check-in of the corrective Visit); resubmission/re-review/re-acceptance (`UC-WO-024`); a Corrective Action list/queue endpoint; `plan_text`/`plan_file_asset_id`/`approved_by`/`approved_at` in any response; any change to `WorkOrder.TeamLeadId`; Cancel Work Order; SLA; notifications/outbox; Reports; Time Correction (held branch, untouched).

- Trace: ST-WO-008; ST-WO-009; ST-CA-001 (Ticket 3, unchanged); CA-API-001; CA-API-002; CA-006/007/008/009/010/011; `docs/02` Definitions "Corrective Action"; UI-060 (partial — Plan/approve only, schedule rework deferred).

### 4.22 CA-API-003 — Corrective Action: Schedule Rework (Coordinator; not `ST-WO-010` "Start Rework")

**Context.** §4.21 Decision (c) deferred `CA-API-003`'s actor to its own future ticket. This ticket implements that scoped slice only: from an `APPROVED` Corrective Action, create and link one `SCHEDULED` corrective Service Visit, atomically, leaving both the Corrective Action's own Status and the Work Order's own Status untouched. It does **not** implement Technician Check-in, rework completion, re-acceptance, Service Report or Time Correction.

The decisions below were each proposed with the baseline text, the options and a recommendation, and approved by explicit directive — none guessed.

**Decision (a) — Coordinator only; a directed decision, not an unambiguous baseline rule.**
`docs/09`'s own text for `CA-API-003` is ambiguous (*"Coordinator/Supervisor authorized"*, §4.21 Decision (c)); `docs/10` UI-060 pairs each role with one verb (*"Team Lead/Supervisor/Coordinator — Plan/approve/schedule rework"*), which reads Coordinator against "schedule rework" specifically. **Per Portfolio Project Owner directive: Coordinator only may execute this action, subject to role and Site scope (`IDataScope.WorkOrders()`).** This mirrors every other Visit-creation action already in this codebase (`WO-API-002`/`ST-WO-001` Schedule, `ST-SV-001` Create Scheduled Visit) — Coordinator is the actor whenever a new Visit is created, never Team Lead/Supervisor. Supervisor's Approve Plan (`CA-API-002`) and the assigned Technician's later Check-in remain separate actions, neither touched by this ticket.

**Decision (b) — Naming: `ScheduleRework`, not `StartRework` — avoids colliding with baseline's own reserved name for a different actor and effect.**
`docs/03` §3's own Work Order Transition Matrix literally reserves the action name **"Start Rework"** for **`ST-WO-010`** — `CORRECTIVE_PLAN_APPROVED → IN_PROGRESS`, **Actor: Assigned Technician**, Guard: *"Corrective Visit assigned/scheduled"*. That is a different transition than this ticket's own Coordinator-driven visit-creation step: ST-WO-010's own guard text (*"Corrective Visit assigned/scheduled"*) describes this ticket's own output as its precondition, confirming the two are sequential, distinct actions, not the same one under two names. Reusing "Start Rework"/"StartRework" for this ticket's domain method, audit action code and route would collide with baseline's own reserved name for the Technician's later action. Per Portfolio Project Owner directive (via the evidence above, not a guess): this ticket's artifacts are named **`ScheduleRework`** throughout — `CorrectiveAction.ScheduleRework(...)`, audit action `CORRECTIVE_ACTION_REWORK_SCHEDULED`, route `POST /api/v1/corrective-actions/{id}/schedule-rework`. "Start Rework"/`ST-WO-010` stays reserved for the future Technician-actor ticket.

**Decision (c) — `ST-WO-010`/transition-matrix reading: no conflict found.**
Read against `docs/03` §3 directly (not a paraphrase): `ST-WO-010`'s own Guard is *"Corrective Visit assigned/scheduled"* — a precondition this ticket satisfies, not a transition this ticket fires. Creating the corrective Visit corresponds to `ST-SV-001` ("Create Scheduled Visit", Actor Coordinator, Guard *"WO permits scheduling; valid team/time"*) — a Service-Visit-level transition that never touches Work Order status. Consequently: **`CA-API-003`/`ScheduleRework` leaves the Work Order in `CORRECTIVE_PLAN_APPROVED`.** The assigned Technician's later Check-in — `ST-WO-010` itself, functionally analogous to `ST-WO-002` Check-in but for the corrective cycle — is the action that moves it to `IN_PROGRESS`, and is explicitly out of this ticket's scope. No gap exists between `ST-WO-009` and `ST-WO-010` in the matrix; nothing here conflicts with it.

**Decision (d) — No Visit-scheduling-time overlap guard; baseline-silent, not baseline-permitting.**
Checked every candidate in code — `WorkOrderScheduleService` (`ST-WO-001`/`ST-SV-001`), `ServiceVisitManageService` (Reschedule/Reassign/DecideMissed), and the shared `TechnicianEligibility.IsEligibleAsync` query (tenant + Technician role + Site scope only) — and found no Visit-scheduling-time overlap check anywhere. `docs/03` §3 confirms this is correct, not an omission: **`ST-SV-001`'s own Guard column** (*"WO permits scheduling; valid team/time"*) **lists no overlap requirement.** The only "no overlap" guards anywhere in the baseline matrix are `ST-SV-002` (Check-in) and `ST-WS-001` (Check-in/create session) — both Check-in-time, both explicitly out of this ticket's scope; the "no active overlap" logic actually implemented in this codebase (`WorkSessionStore`/`TechnicianCheckInService`, BR-05) is that Check-in-time guard, not a Visit-scheduling-time one. Per Portfolio Project Owner directive: **Schedule Rework enforces no scheduling-time overlap check**, reusing only what `ST-SV-001` itself requires — active team present, technician eligibility (`IsTechnicianEligibleAsync`, shared with Schedule and Reassign), and a valid time window (start required, end required, end ≥ start). This is recorded as a documented absence, not a claim that double-booking is affirmatively permitted; preventing it would be a separate, undecided business rule, not this ticket's to invent.

**Decision (e) — `corrective_action.row_version` added by migration; Schedule Rework's own concurrency token, not the Work Order's.**
`§4.21`'s own concurrency note explains why Submit Plan/Approve Plan use the Work Order's RowVersion: both genuinely transition the Work Order's own Status, giving it a real, EF-triggered compare-and-swap. **`ScheduleRework` transitions neither the Corrective Action's Status nor the Work Order's** — so the Work Order's RowVersion would never actually change, the same limitation `CostSummaryStore.SaveUpdateAsync`'s own remarks record for Cost Summary's re-edit case (*"the Work Order's own RowVersion never advances… cannot detect a lost update"*). `docs/05`'s own field list stops at `CA-012`; no `corrective_action.row_version` is documented or previously migrated. Per Portfolio Project Owner directive, confirmed via schema check before deciding (migration `AddCorrectiveActionRowVersion`): a genuine `row_version` (SQL Server `rowversion`, `IsRowVersion()`) column is added to `corrective_action`, mirroring exactly why `CostSummary` needed its own RowVersion for the identical reason (§4.18 Decision 4's own two-phase design, here made unnecessary by using a real token instead). Because `ScheduleRework` genuinely mutates the Corrective Action row (`CorrectiveServiceVisitId: null → a real id`), this is a normal, fully EF-triggered optimistic-concurrency check — proven end to end by `TwoConcurrentScheduleRework_ExactlyOneSucceeds_WithExactlyOneVisit_AndNoDuplicateAudit` (real LocalDB, no fake/mocked concurrency): the loser's UPDATE affects zero rows and its Service Visit insert rolls back in the same transaction. `WorkOrderResponse`/`WorkOrderDto` gain `correctiveActionRowVersion` (mirroring `costSummaryRowVersion`'s identical "recover a token across page reloads" purpose) — without it, an Angular client would have no way to learn Schedule Rework's own If-Match token before its first call.

**CA-API-003 Schedule Rework.**
`POST /api/v1/corrective-actions/{id}/schedule-rework` — Actor: Coordinator (Decision a) — If-Match required (**the Corrective Action's own RowVersion**, Decision e) — body `{ assignedTeamId, assignedTechnicianId, scheduledStartAt, scheduledEndAt }` (same shape as `NewVisitScheduleRequest`, reused). Check order (identical convention to every other command in this codebase): 400/401/403 (role gate) → 404 (scope: the linked Work Order in `IDataScope.WorkOrders()`, non-leaking) → 409 `CONCURRENCY_CONFLICT` (stale Corrective Action RowVersion) → 409 `STATE_CONFLICT` (Corrective Action not `APPROVED`, or `CorrectiveServiceVisitId` already set) → 422 `VALIDATION_FAILED` (team missing; start/end missing or end < start; technician not an active Technician Site-scoped to this Work Order — Decision d). Success, one transaction: a new `ServiceVisit` (`VisitType: CORRECTIVE`, `Status: SCHEDULED`), `CorrectiveAction.CorrectiveServiceVisitId` set once, one `CORRECTIVE_ACTION_REWORK_SCHEDULED` audit row. Neither `CorrectiveActionStatus` nor `WorkOrder.Status` changes (Decision c).

**Audit.** `CORRECTIVE_ACTION_REWORK_SCHEDULED` — `entity_type` `CORRECTIVE_ACTION` (same convention as §4.21's own two action codes), `fromState`/`toState` both `APPROVED` (records the write, not a transition — the same shape already used by `COST_SUMMARY_PREPARED`/`COST_SUMMARY_UPDATED`, §4.18). Self-named per the established `<ENTITY>_<PAST_TENSE_VERB>` convention (no literal baseline action-code string exists).

**Authorization matrix.**

| Actor | Action | Scope |
|---|---|---|
| Coordinator | `CA-API-003` Schedule Rework | `IDataScope.WorkOrders()` (site-wide) via the linked Work Order |
| Coordinator of another tenant or without Site scope | Schedule Rework | `404` (non-leaking) |
| Requester/Approver/Technician/Team Lead/Supervisor/Administrator | Schedule Rework | `403 ACCESS_DENIED` |

**Angular.** The Work Order detail page gains a "Schedule Rework" form, gated purely on `correctiveActionStatus === 'APPROVED' && correctiveServiceVisitId === null` and the signed-in user holding `COORDINATOR` (UX only, backend re-checks regardless) — sends the Corrective Action's own `correctiveActionRowVersion` as If-Match (never the Work Order's `rowVersion`), disables while in flight, and on `409` reloads the current Work Order.

**Not in this round:** Technician Check-in (`ST-WO-010` itself — "Start Rework" in the baseline's own naming, Decision b); rework completion; re-acceptance (`UC-WO-024`); Service Report; Time Correction (held branch, untouched); a Visit-scheduling-time overlap guard (Decision d); a Corrective Action list/queue endpoint; `plan_text`/`plan_file_asset_id`/`approved_by`/`approved_at` in this response (unchanged from §4.21).

- Trace: `ST-SV-001` (Visit creation, reused); `ST-WO-010` (read only, not implemented — Decisions b/c); CA-API-003; CA-012 (`corrective_service_visit_id`); UI-060 (partial — schedule rework only, plan/approve already covered by §4.21).

### 4.23 ST-WO-010 — Technician Check-in / "Start Rework" on the corrective Visit

**Context.** §4.22 Decision (c) already established that `CA-API-003`/`ScheduleRework` leaves the Work Order at `CORRECTIVE_PLAN_APPROVED`, and that `ST-WO-010` — the assigned Technician's Check-in on the corrective Visit — is the separate, later transition to `IN_PROGRESS`. This ticket implements exactly that gap, and confirms a reading that was only inferred (not yet verified against the endpoint catalog) in §4.22.

**No new API, no new UI — confirmed, not guessed.** `docs/09` (RR-API-001) §3's own Endpoint Catalog lists exactly one Check-in endpoint anywhere: **`WS-API-001 POST /api/v1/service-visits/{id}/check-in`, Actor "Assigned Technician"**. `CA-API-003` is catalogued only as "Create corrective Visit." No separate "Start Rework" API exists. `docs/03` §3's own `ST-WO-010` row (`CORRECTIVE_PLAN_APPROVED → IN_PROGRESS`, Guard *"Corrective Visit assigned/scheduled"*) is therefore not a new action — it is what already happens when `WS-API-001` succeeds on a `SCHEDULED` corrective Visit. Every part of the existing S3-001 Check-in implementation was already Visit-type-agnostic and required no change: the route/controller (`ServiceVisitsController.CheckIn`), the Service Visit's own state guard (`ST-SV-002`, matches on status only), `IDataScope.AssignedServiceVisits` (no `VisitType` filter), BR-05's active-Work-Session-overlap guard (app-level `HasActiveSessionAsync` plus the DB-level unique filtered index `IX_work_session_tenant_id_technician_id_active`, both keyed only on tenant/technician), the concurrency mechanism, the `WorkSessionCheckIn` authorization policy, and the Angular "My Visits" check-in flow (its list query has no `VisitType` filter either).

**What was actually missing — the entire scope of this ticket.**

1. **`WorkOrderStatusTransitions.All` had no `CorrectivePlanApproved → InProgress` entry.** `IsAllowed(from, to)` matches only `(From, To)` pairs, so both `TechnicianCheckInService`'s own pre-check and `WorkOrder.BeginWork()`'s own guard rejected a corrective Check-in with `409 STATE_CONFLICT` before this change. Added `new("ST-WO-010", WorkOrderStatus.CorrectivePlanApproved, "StartRework", WorkOrderStatus.InProgress)` — the `"StartRework"` action name was deliberately reserved for exactly this ticket by §4.22 Decision (b), which avoided it for the Coordinator-side ticket for this reason.
2. **A latent audit defect, found and fixed, not present in the baseline.** `WorkOrderAudit.WorkStarted` hardcoded `fromState = SCHEDULED` — correct for `ST-WO-002`, but wrong for a Corrective Check-in, whose real prior state is `CORRECTIVE_PLAN_APPROVED`. No existing test caught this (the only assertion on this audit row before this ticket checked action-code presence, never its `fromState`/`toState` fields). Fixed by adding a second, distinct factory, `WorkOrderAudit.ReworkStarted`, and having `TechnicianCheckInService` read `workOrder.Status` immediately before calling `BeginWork()` to dispatch to the correct one — never a single factory taking a runtime `fromState` parameter, keeping each factory single-purpose per this codebase's established one-code-per-transition-ID convention.
3. **Audit action code: `WORK_ORDER_REWORK_STARTED`, a new distinct code — Portfolio Project Owner directive, confirmed before implementation, not the default.** No baseline-literal action code exists for `ST-WO-010`. Every other `WorkOrderAudit` factory in this codebase maps 1:1 to one transition ID; reusing `WORK_ORDER_STARTED` for both `ST-WO-002` and `ST-WO-010` would have been the only exception.
4. **An existing, previously-passing test was corrected, not silently left inconsistent.** `WorkOrderCheckInDomainTests.BeginWork_FromAnyStateOtherThanScheduled_IsDenied_AndChangesNothing` (renamed to `BeginWork_FromAnyStateOtherThanScheduledOrCorrectivePlanApproved_IsDenied_AndChangesNothing`) asserted `CorrectivePlanApproved` was a denied source state for `BeginWork()` — true before this ticket, false after. Removed from that Theory; a new `BeginWork_FromCorrectivePlanApproved_MovesToInProgress` test added alongside the existing `BeginWork_FromScheduled_MovesToInProgress`.
5. **No new guard beyond what Check-in already enforces.** `ServiceVisit.Status == SCHEDULED` is itself sufficient proof that `ScheduleRework` already ran (the corrective Visit would not exist, or would not still be `SCHEDULED`, otherwise) — nothing re-checks `CorrectiveAction.Status` at Check-in time, matching baseline's own guard text for `ST-WO-010` ("Corrective Visit assigned/scheduled", not "Corrective Action approved").

**Audit.** Two distinct Work-Order-entity audit action codes now exist for "work begins": `WORK_ORDER_STARTED` (`ST-WO-002`, `fromState = SCHEDULED`) and `WORK_ORDER_REWORK_STARTED` (`ST-WO-010`, `fromState = CORRECTIVE_PLAN_APPROVED`) — both `toState = IN_PROGRESS`, both written in the same transaction as the unchanged `SERVICE_VISIT_CHECKED_IN` audit, both triggered by the same physical Check-in action, distinguished only by which transition actually fired. No reason applies to either.

**Authorization matrix.** Unchanged from S3-001 — `WorkSessionCheckIn = [Technician]`, `IDataScope.AssignedServiceVisits` (assigned Technician, current Site scope). A Corrective Visit adds no new actor, scope, or policy.

**Angular.** No changes. The existing "My Visits" list and Check-in button (`my-visits.component.ts`, `my-visits.service.ts`) already show and act on any assigned `SCHEDULED` Visit regardless of `visitType` — a corrective Visit created by Schedule Rework already appears and is already clickable once the Coordinator has scheduled it.

**Not in this round:** rework completion (Check-out / Submit Work Summary on the corrective Visit — reuses the existing `WO-API-008`/`WO-API-009` flow unmodified, but not exercised or changed by this ticket); re-acceptance (`UC-WO-024`); Service Report; Time Correction (held branch, untouched); Visit-scheduling-time overlap (§4.22 Decision d, still undecided, still not this ticket's to invent).

- Trace: `ST-WO-010`; `ST-SV-002`/`ST-WS-001` (reused, unchanged); `WS-API-001`; BR-05 (reused, unchanged).

---

## 3. Common Conventions (as implemented)

### 3.1 Authorization policies

| Policy | Roles | Source |
|---|---|---|
| MasterData.Read | All approved roles | RR-REQ-001 §3/§13; S1-003 |
| MasterData.Manage | ADMINISTRATOR | RR-REQ-001 §3 ("Master data ... configuration") |
| RepairRequest.Read | REQUESTER, APPROVER, COORDINATOR, TECHNICIAN, TEAM_LEAD, SUPERVISOR | FR-09; S1-003 |
| RepairRequest.Draft | REQUESTER | RR-REQ-001 §13 |
| RepairRequest.Review | APPROVER — Approve/Reject (S1-008); the assigned-approver and self-decision rules are enforced by the command | RR-REQ-001 §13; DEC-PRE-S1-008-01/03 |
| Routing.Recovery | ADMINISTRATOR — routing-issue list and Retry Routing only; never Repair Request detail, Approve or Reject | DEC-PRE-S1-007R-07/10 |
| WorkOrder.Schedule | COORDINATOR | S2-003; ST-WO-001; UC-WO-003 |
| ServiceVisit.Manage | COORDINATOR | S2-003; ST-SV-004..009; UC-WO-005..009 |
| MyVisits.Read | TECHNICIAN | S3-001; UI-040 |
| WorkSession.CheckIn | TECHNICIAN | S3-001; ST-WS-001; BR-05; UC-WO-016 |
| WorkSession.Pause | TECHNICIAN | S3-002; ST-WS-002; UC-WO-012 |
| WorkSession.Resume | TECHNICIAN | S3-003; ST-WS-003; UC-WO-012 |
| WorkSession.CheckOut | TECHNICIAN | S3-004; ST-WS-004; UC-WO-016 |
| WorkSession.Read | TECHNICIAN | S3-002 |
| CostSummary.Prepare | TEAM_LEAD — Prepare (create, then edit until reviewed) a COMPLETED Work Order's Cost Summary: `PUT /api/v1/work-orders/{id}/cost-summary` (CST-API-001), `WorkOrdersController.PrepareCostSummary`. Data scope: `IDataScope.WorkOrders()` (tenant + the caller's own Site scopes through the Work Order's Repair Request), applied by `CostSummaryStore.LoadWorkOrderInScopeAsync`; state guards in `CostSummaryService` | Ticket 4a; §4.18; BR-08 |
| CostSummary.Read | TEAM_LEAD, SUPERVISOR — read a Work Order's Cost Summary: `GET /api/v1/work-orders/{id}/cost-summary` (technical addition, no baseline endpoint), `WorkOrdersController.GetCostSummary`. Data scope: the same `IDataScope.WorkOrders()`, applied inside `CostSummaryStore.GetCostSummaryDtoAsync`; out of scope or not yet prepared → the same non-leaking `404`. Deliberately narrower than `WorkOrder.Read` | Ticket 4b; §4.19 |
| CostSummary.Review | SUPERVISOR — mark a COMPLETED Work Order's unreviewed Cost Summary as reviewed: `POST /api/v1/work-orders/{id}/review-cost-summary` (CST-API-002), `WorkOrdersController.ReviewCostSummary`. Data scope: `IDataScope.WorkOrders()` via `CostSummaryStore.LoadWorkOrderInScopeAsync`; object-level rule in `CostSummaryService`: the reviewer must not be the preparer (`403`, even holding both roles) | Ticket 4b; §4.19; BR-08 |
| CostSummary.ReadPendingReview | SUPERVISOR — list COMPLETED Work Orders whose Cost Summary is unreviewed: `GET /api/v1/work-orders/pending-cost-summary-review` (technical addition), `WorkOrdersController.PendingCostSummaryReview`. Data scope: `IDataScope.WorkOrders()` inside `CostSummaryStore.ListPendingReviewAsync` | Ticket 4b; §4.19 |
| WorkOrder.Close | SUPERVISOR — close a COMPLETED Work Order: `POST /api/v1/work-orders/{id}/close` (WO-API-010), `WorkOrdersController.Close`. Data scope: `IDataScope.WorkOrders()` via `WorkOrderCloseStore.LoadInScopeAsync`; the Close guards live in `WorkOrderCloseService`; no Separation of Duties | Ticket 5; §4.20; ST-WO-006; BR-08 |

- Every non-anonymous endpoint is deny-by-default and needs an authenticated principal (JWT bearer, DEC-PS1-004).
- Tenant, actor and scope are always derived on the server (D-11 / BR-16). Unknown JSON members such as `tenantId`, `customerId` or `status` are ignored.
- **How the Cost Summary and Close policies are enforced (all five rows above, and every other policy in `AuthorizationPolicies.AllowedRoles`).** `AuthorizationPolicies.AllowedRoles` (`RepairRequest.Application`, `Security/AuthorizationPolicies.cs`) is the single role map. `AddRepairRequestAuthorization` (`RepairRequest.Api`, `Authorization/AuthorizationServiceCollectionExtensions.cs`) registers one ASP.NET Core policy per entry from a `RoleCapabilityRequirement` evaluated by `RoleCapabilityAuthorizationHandler`, alongside `ResolvedUserAuthorizationHandler`; each controller action applies its policy with `[Authorize(Policy = AuthorizationPolicies.<Name>)]`. The role gate comes first (`403`); tenant/Site scope and object-level rules are then enforced in the Application service/store (`404` for out of scope). Angular route guards and button visibility are UX convenience only. `AuthorizationPoliciesTests` pins the roles of each policy and the total catalog size (27).
- **Not yet itemised in the table above (pre-existing gap, out of this revision's scope):** eight further policies exist in `AuthorizationPolicies.AllowedRoles` — `RepairRequest.Convert`, `WorkOrder.Read`, `WorkOrder.SubmitWorkSummary`, `WorkOrder.SubmitForAcceptance`, `WorkOrder.ReadWorkSummary`, `WorkOrder.ReadEligibleAcceptanceContacts`, `WorkOrder.Accept` and `WorkOrder.Reject`. §4.6, §4.10, §4.15, §4.16 and §4.17 describe their actors.

### 3.2 Error contract (RR-API-001 §2)

- Controlled errors return `application/problem+json` with `code` and `correlationId`.

| HTTP | code | When |
|---|---|---|
| 400 | BAD_REQUEST | Malformed input; missing or invalid `If-Match`; invalid `page`/`pageSize`/`status` query; timestamp without explicit UTC offset |
| 401 | UNAUTHENTICATED | No valid principal. Auth endpoints return one generic 401 for all authentication failures |
| 403 | ACCESS_DENIED | Role can never perform the action (policy) |
| 404 | NOT_FOUND | Nonexistent **or** out-of-scope id — identical, non-leaking response |
| 409 | CONCURRENCY_CONFLICT | `If-Match` no longer matches the current rowversion; nothing written |
| 409 | STATE_CONFLICT | Illegal state (e.g. not DRAFT, already active/inactive), dependency guard (`activeChildCount` extension), non-CLEAN download |
| 422 | VALIDATION_FAILED | Field/master/business validation; `errors` map of field → messages |

### 3.3 ETag / If-Match

- The ETag is a strong tag holding the base64 SQL rowversion (8 bytes).
- Updates and state changes need exactly one strong `If-Match` value. `*`, weak tags and lists are rejected with 400.
- Detail and successful command responses return the current `ETag`. Response bodies also carry `rowVersion`.

### 3.4 Paging (RR-API-001 §7/§9)

- Query string: `page` (1-based) and `pageSize`. A value below 1 → 400.
- `pageSize` defaults to `Paging:DefaultPageSize` and is clamped to `Paging:MaxPageSize`. Current appsettings: 20 / 100. These are deployment configuration, not business rules.
- Response body: `{ items, page, pageSize, totalCount }`. Tenant/site scope is applied before paging, and paging runs in SQL.

### 3.5 Known framework behaviour (documented, not normalized)

These responses come from ASP.NET Core before application code runs. Their bodies may not follow the §3.2 problem shape (`code`/`correlationId` may be absent). No implementation change is implied.

| Situation | Possible response |
|---|---|
| Request body exceeds the endpoint request-size limit (FILE-API-001: 10 MB + 1 MB multipart framing allowance) | **413 Payload Too Large** from the server/framework |
| Multipart body exceeds the form limit or is malformed | **400** from framework model binding |
| Upload sent with a non-multipart content type | **415 Unsupported Media Type** (or 400) from the framework |
| JSON body not parseable / wrong JSON types | **400** framework validation problem |

---

## 4. Endpoint Details

### 4.1 Repair Request Draft (catalogued; implementation notes)

**RR-API-001 POST `/api/v1/repair-requests` — RepairRequest.Draft**
- Body (all optional while DRAFT, RR-DD-001 Y@Submit): `siteId`, `equipmentId`, `requestCategoryCode`, `priorityCode`, `requestContactId`, `description`, `preferredStartAt`, `preferredEndAt`.
- Timestamps must be ISO-8601 with an explicit offset; otherwise 400.
- `requestCategoryCode` / `priorityCode` are tenant-scoped seeded lookup codes (DEC-PRE-S1-007-01/02/03). They are trimmed, matched case-insensitively and stored in canonical form.
- `requestContactId` must be an existing user of the caller's tenant with business Site scope on the selected Site: a non-ADMINISTRATOR role and a Site assignment (DEC-PRE-S1-007-04). The ACTIVE-user check is deferred (REQ-FU-USR-001).
- `locationId` is not accepted in Sprint 1 (DEC-PRE-S1-007-05); tenant, requester, status and `requestNo` are never accepted.
- `201 Created` with `Location` and `ETag`.
- Response (shared by RR-API-001/002/004/005): `{ id, status, requestNo, siteId, equipmentId, requestCategoryCode, priorityCode, requestContactId, description, preferredStartAt, preferredEndAt, createdBy, submittedAt, rowVersion }`.
- Errors:
  - Site outside the caller's business Site scope → 404.
  - 422 for:
    - inactive Site/Equipment, Equipment not in Site, or Equipment without Site;
    - description >2000, or end < start;
    - Category/Priority code that is unknown, inactive, too long, or not printable ASCII;
    - request contact without a Site, or an empty contact id;
    - a contact that is not an eligible user. The same message is returned for an unknown, cross-tenant or out-of-scope user.
- Audit `REPAIR_REQUEST_DRAFT_CREATED`.
- Trace: UC-RR-001, ST-RR-001, TC-RR-001, TC-SEC-001.

**RR-API-002 PATCH `/api/v1/repair-requests/{id}` — RepairRequest.Draft, If-Match**
- The body carries the complete editable field set of RR-API-001 (including `requestCategoryCode`, `priorityCode`, `requestContactId`), as saved by the Draft form; an omitted field is saved as empty.
- The whole resulting Draft is validated on every save (DEC-S1-005-E2) with the RR-API-001 rules above.
- Not owner / out of scope / missing → 404. Not DRAFT → 409 STATE_CONFLICT. Stale → 409 CONCURRENCY_CONFLICT.
- No-change saves write and audit nothing. Otherwise audit `REPAIR_REQUEST_DRAFT_UPDATED` with old/new values.
- Trace: ST-RR-001, TC-RR-001, TC-SEC-002.

**RR-API-004 GET `/api/v1/repair-requests/{id}` — RepairRequest.Read**
- Detail within the caller's approved Repair Request scope (DEC-S1-005-E3; S1-003). The response shape is as in RR-API-001.
- `ETag` returned. Out of scope → 404.
- Trace: FR-09, TC-SEC-001.

**RR-API-005 POST `/api/v1/repair-requests/{id}/submit` — RepairRequest.Draft, If-Match (IMPLEMENTED S1-007)**
- **Body:** `{ duplicateContinuationReason: string | null }`. The body may be omitted. A missing or unusable `If-Match` → 400.
- **Checks, in order:**
  1. Not the owner (`created_by`), out of scope or missing → 404 (identical response).
  2. Stale `If-Match` → 409 CONCURRENCY_CONFLICT.
  3. Not DRAFT → 409 STATE_CONFLICT.
  4. Selected Site no longer in the caller's business Site scope → 404.
  5. One 422 VALIDATION_FAILED listing every failure:
     - a missing Site, Category, Priority, request contact, description, preferred start or preferred end;
     - an inactive Site/Equipment or Equipment not in the Site;
     - an unknown/inactive Category or Priority;
     - an ineligible contact (RR-API-001 rules);
     - `attachments` when there is no CLEAN JPG/JPEG/PNG attachment. PENDING and FAILED attachments neither count nor block (DEC-PRE-S1-007-06/-07).
  6. BR-14 duplicate: an active request (SUBMITTED, UNDER_REVIEW, APPROVED, CONVERTED) of the same tenant, Site and Category, with the same Equipment (empty matches only empty), submitted in the last 24 hours. Without a reason → 422 VALIDATION_FAILED with an error on `duplicateContinuationReason` and a `duplicateCount` extension only (DEC-PRE-S1-007-09/-10).
  - The duplicate check runs only when steps 1–5 pass.
- **Continuation reason:** trimmed; longer than 1000 characters → 422. It is stored and audited only when a duplicate exists, and otherwise ignored.
- **Success → `200`** with the RR-API-001 response shape, `status = UNDER_REVIEW` when System routing assigned an approver right after the commit, otherwise `status = SUBMITTED` (S1-007R, DEC-PRE-S1-007R-01; see §4.6), `requestNo` `RR-{yyyy}-{000000}` (per tenant, per UTC year), `submittedAt` (the SLA start marker only), and a fresh `ETag`.
  - No SLA due, risk or breach fields; no notification (DEC-PRE-S1-007-11/-12). Approval routing runs after the Submit commit, in its own transaction (S1-007R, §4.6).
  - **After the Submit commit, Submit never fails.** If routing does not complete, or routing and the final-state read-back throw, the response is still `200` with the committed state: `status = SUBMITTED`, the allocated `requestNo`, `submittedAt` and the committed `ETag`. `UNDER_REVIEW` is returned only when routing succeeded. The failure is logged with the correlation id; recovery is ROUTING-API-002. Repeating the Submit → 409.
- **Atomicity:** Request No allocation (per tenant-year counter, never MAX + 1), the DRAFT → SUBMITTED transition and the audit `REPAIR_REQUEST_SUBMITTED` (from/to state, `requestNo`, `submittedAt`, `duplicateCount`, reason) commit in one transaction.
- **Concurrent or deadlocked Submit** of the same request → 409; exactly one succeeds.
- **Concurrent Submits of different Drafts with the same duplicate key** are serialized per key inside the Submit transaction (RR-DEC-001 DEC-PRE-S1-007-09, implementation note).
  - At most one of them proceeds without a continuation reason; the other receives the duplicate warning (422 with `duplicateCount`) and stays DRAFT.
  - Submits with other keys are not delayed by this.
  - If the key cannot be locked within the wait limit → 409 CONCURRENCY_CONFLICT, and nothing is written.
- **Any failed Submit** writes nothing: no Request No, no `submittedAt`, no audit.
- **Resubmission (S1-010):** the same endpoint and checks apply to a DRAFT returned for correction. It keeps its `requestNo` and `submittedAt`, allocates no Request No, re-runs the duplicate check without matching itself, and audits `REPAIR_REQUEST_RESUBMITTED`; see §4.9.
- Trace: UC-RR-002, ST-RR-002, BR-01/03/14/16, D-12, TC-RR-003/004, TC-SEC-001/002.

### 4.2 Attachments / Files

**FILE-API-001 POST `/api/v1/repair-requests/{id}/attachments` — RepairRequest.Draft**
- `multipart/form-data` with field `file`. A missing `file` field → 400.
- Caller must own the request (`created_by`) within scope, otherwise 404. Request must be DRAFT, otherwise 409 STATE_CONFLICT.
- File rules (422 on field `file`):
  - PDF/JPG/JPEG/PNG only.
  - Extension, declared content type and content signature must agree.
  - Non-empty, ≤10 MB.
- Server-generated opaque storage key (DEC-S1-006-SK). The display filename is sanitized. `storage_reference` is never returned.
- Every new FileAsset starts `PENDING` (DEC-PS1-005).
- `access_scope_code = REPAIR_REQUEST` is set by the server (DEC-S1-006-F4).
- No business count limit (DEC-S1-006-F1).
- `201 Created`, `Location: /api/v1/files/{fileAssetId}`, no ETag.
- Response: `{ attachmentId, fileAssetId, fileName, mimeType, sizeBytes, malwareScanStatus, uploadedAt }`.
- Audit `REPAIR_REQUEST_ATTACHMENT_ADDED`.
- Framework 413/400/415 behaviour: §3.5.
- Trace: UC-RR-001, TC-RR-002, RR-ARCH-001 §11.

**FILE-API-003 GET `/api/v1/repair-requests/{id}/attachments` — RepairRequest.Read (NEW IN CATALOG)**
- Query: `page`, `pageSize` (§3.4). Repair Request outside the caller's Repair Request scope or nonexistent → 404.
- Returns a bounded page of attachment **metadata only**, in upload order (sequential attachment id). PENDING, CLEAN and FAILED items are all listed with their `malwareScanStatus`.
- Item shape is the same as the FILE-API-001 response. No file content and no storage reference.
- Response: `{ items, page, pageSize, totalCount }`.
- Trace: DEC-S1-006-F1, DEC-S1-006-PG, DEC-S1-006-F4, TC-RR-002, TC-SEC-001.

**FILE-API-002 GET `/api/v1/files/{fileAssetId}` — RepairRequest.Read**
- The file must be attached to a Repair Request within the caller's scope, otherwise 404.
- Only `CLEAN` files are served. PENDING/FAILED → 409 STATE_CONFLICT.
- Response headers: server canonical content type, attachment disposition with framework-encoded filename, `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`.
- Audit `REPAIR_REQUEST_ATTACHMENT_DOWNLOADED`.
- Trace: RR-REQ-001 §9, RR-ARCH-001 §11, TC-RR-002, TC-SEC-001.

**Not provided:**
- No attachment removal endpoint (DEC-S1-006-F3; DEFERRED).
- No malware scan trigger endpoint. The production scanning provider and background Worker are DEFERRED (DEC-PS1-005, DEC-S1-006-F2).
- A Development/Testing-only fake scanner and PENDING scan runner exist as test infrastructure only (DEC-PRE-S1-007-08). They are opt-in, refuse to start in any other environment, and provide no malware protection. Production keeps `NotConfiguredMalwareScanner`, so files stay PENDING and unusable.

### 4.3 Authentication (DEC-PS1-004)

- **AUTH-API-001 login**
  - Body `{ email (required, e-mail, ≤256), password (required) }`.
  - `200 { accessToken, tokenType: "Bearer", expiresAt }`.
  - The refresh token is set only as cookie `__Secure-rr-refresh-token`: HttpOnly, Secure, SameSite=Strict, Path=`/api/v1/auth`. It is never in the body.
  - Model validation → 400. Any authentication failure → generic 401 UNAUTHENTICATED, and the cookie is cleared.
- **AUTH-API-002 refresh:** reads the refresh cookie. Success → 200 with the same body and a rotated cookie. Missing/invalid/expired/revoked → generic 401, cookie cleared.
- **AUTH-API-003 revoke:** revokes the refresh token if the cookie is present, always clears the cookie, returns `204 No Content`.
- All three set `Cache-Control: no-store`.
- No self-registration, password reset or rate limiting endpoints exist. Rate limits remain INFORMATIONAL per RR-DEC-001 §4 item 10.
- Trace: DEC-PS1-004. TC-AUTH-* not yet authored in RR-TC-001; automated tests exist.

### 4.4 Master Data (DEC-PS1-001/002/003/013/014/015; DEC-S1-004-D1..D4)

**Scope:**
- Reads use the caller's master-data scope: Customers with an assigned Site, and assigned Sites; ADMINISTRATOR sees the whole tenant.
- Out-of-scope ids → 404.

**List endpoints:**
- `page`, `pageSize`, optional `status=ACTIVE|INACTIVE`. Any other status → 400. Omitted → both statuses listed.

**Bodies:**
- Create/PATCH: Customer `{ customerCode }`, Site `{ siteCode }`, Equipment `{ equipmentCode }`.
- Deactivate: `{ reason }`. Activate: no body.
- Parent ids, tenant and status are never accepted from the body.

**Responses:**
- Customer `{ id, customerCode, status, deactivateReason, rowVersion }`.
- Site `{ id, customerId, siteCode, status, deactivateReason, rowVersion }`.
- Equipment `{ id, siteId, equipmentCode, status, deactivateReason, rowVersion }`.
- Create → `201` with `Location` and `ETag`; other commands → `200` with `ETag`.

**Rules:**

| Command | Outcome |
|---|---|
| Create | Code required, unique within scope (DEC-PS1-015) → else 422. Site/Equipment parent must be ACTIVE → else 422. Parent out of scope → 404 |
| Update (PATCH) | If-Match. Only the code changes. A Site never moves to another Customer, nor Equipment to another Site (DEC-S1-004-D3). Unchanged code → nothing written or audited |
| Activate | If-Match. Already ACTIVE → 409 STATE_CONFLICT. Site/Equipment with inactive parent → 422 (DEC-S1-004-D2). Success clears `deactivateReason`; the previous reason stays in audit history (DEC-S1-004-D1) |
| Deactivate | If-Match. Reason required (DEC-PS1-014) → else 422. Already INACTIVE → 409. Customer with active Sites / Site with active Equipment → 409 STATE_CONFLICT with `activeChildCount`; no cascade (DEC-PS1-013). No open-Repair-Request guard (DEC-S1-004-D4) |

- Guard checks and the change run in one SERIALIZABLE transaction.
- Trace: DEC-PS1-001..003/013..015. TC-CUST-*/TC-SITE-*/TC-EQP-* not yet authored in RR-TC-001; automated tests exist.

---

### 4.6 Approval Routing (S1-007R — IMPLEMENTED, b1cad73)

**System routing after Submit (ST-RR-003; DEC-PRE-S1-007R-01..03/05/06/09)**
- Runs immediately after the Submit commit, in its own transaction. A routing failure or error never fails the Submit.
- **Route selection:** the exact ACTIVE Site route for the request's Category, else the ACTIVE tenant-default route, else failure. More than one ACTIVE route at that level is a failure; the code never picks one. An ACTIVE Site route without a valid step is a failure; it does not fall back to the tenant default.
- **Approver:** the route must have exactly one step, number 1, role APPROVER.
  - With a specific approver: that user must hold APPROVER with business Site scope on the request's Site and must not be the request's creator.
  - Without one: exactly one such user other than the creator is assigned; zero or several is a failure.
- **Success:** approval row PENDING with `assigned_approver_id` and `routed_at`; request SUBMITTED → UNDER_REVIEW; audit `REPAIR_REQUEST_ROUTED`.
- **Failure:** request stays SUBMITTED; audit `REPAIR_REQUEST_ROUTING_FAILED` with the code.
  - ROUTE_NOT_FOUND / ROUTE_AMBIGUOUS / ROUTE_STEP_INVALID write no approval row.
  - APPROVER_NOT_ELIGIBLE / APPROVER_NOT_FOUND / APPROVER_AMBIGUOUS write an approval row with the route and step, no approver, and `routing_failure_code`.
  - A later attempt that fails at route level removes an earlier unassigned approver-level failure row, so the current state matches the latest failure. The earlier code, route and step stay in the append-only `REPAIR_REQUEST_ROUTING_FAILED` history. An assigned or decided approval is never removed.
- **Routing audit actor:** System (fixed system identity; ST-RR-003). The user whose command initiated routing (the submitting requester, or the ADMINISTRATOR on retry) is recorded only as `initiatedBy` in the audit value document, next to `trigger` (`SUBMIT` / `ADMIN_RETRY`) and the correlation id.
- An unexpected routing error (not a routing rule outcome) rolls back only the routing transaction; no failure code is recorded or fabricated.
- The approver ACTIVE user-state check is deferred (REQ-FU-USR-001).

**ROUTE-API-001..005 `/api/v1/approval-routes` — MasterData.Manage (ADMINISTRATOR, own tenant)**
- **Create body:** `{ requestCategoryCode, siteId?, approverRoleCode, approverUserId? }`. Tenant, status, route id and step number are never accepted.
- **Create validation (422):**
  - Category must be an ACTIVE code of the tenant (canonicalized);
  - `siteId`, if given, must be an ACTIVE Site of the tenant;
  - `approverRoleCode` must be `APPROVER`;
  - `approverUserId`, if given, must be an APPROVER of the tenant;
  - no other ACTIVE route with the same Category and Site (or tenant default).
- `201 Created` with `Location` and `ETag`.
- **Response:** `{ id, requestCategoryCode, siteId, status, stepNo, approverRoleCode, approverUserId, rowVersion }`.
- **List / detail:** list is paged with optional `status=ACTIVE|INACTIVE`; detail returns an `ETag`. Another tenant's route → 404.
- **Activate:** If-Match. Already ACTIVE → 409 STATE_CONFLICT. References re-validated → 422. Another ACTIVE route for the key → 422.
- **Deactivate:** If-Match, no body. Already INACTIVE → 409 STATE_CONFLICT.
- No update endpoint. Audits `APPROVAL_ROUTE_CREATED / ACTIVATED / DEACTIVATED`.

**ROUTING-API-001 GET `/api/v1/routing-issues` — Routing.Recovery (ADMINISTRATOR, own tenant)**
- A narrow routing-operations exception to the S1-003 ADMINISTRATOR no-business-read rule (DEC-PRE-S1-007R-10).
- **Lists:** SUBMITTED requests with no approval assigned: never routed, or routing failed.
- **Paging and sort:** `page`, `pageSize` (§3.4); sorted by `submittedAt`, then id.
- **Item fields only:** `{ repairRequestId, requestNo, siteId, requestCategoryCode, submittedAt, lastRoutingFailureCode, rowVersion }`.
  - `lastRoutingFailureCode` is **nullable**: the code of the latest `REPAIR_REQUEST_ROUTING_FAILED` audit, or null when the request was never routed or its routing attempt ended in an unexpected error before a failure was recorded. No code is fabricated.
  - `requestNo`, `siteId`, `requestCategoryCode` and `submittedAt` are always set for SUBMITTED requests.
  - No description, requester/contact, attachments or audit history.

**ROUTING-API-002 POST `/api/v1/repair-requests/{id}/retry-routing` — Routing.Recovery, If-Match, no body**
- Re-runs the same server-side routing; the caller never chooses a route or approver.
- **Success → `200`:** `{ repairRequestId, status, routingFailureCode, rowVersion }` + `ETag`.
  - Routed: `status = UNDER_REVIEW`, `routingFailureCode = null`.
  - Failed again: `status = SUBMITTED` with the new code.
- **Errors:**
  - 400 missing or invalid If-Match;
  - 401;
  - 403 non-ADMINISTRATOR;
  - 404 unknown or other tenant;
  - 409 CONCURRENCY_CONFLICT stale token, or the routing lock was not granted within the configured wait (nothing is written and the retry can be repeated);
  - 409 STATE_CONFLICT not SUBMITTED, or approver already assigned.
- **Concurrency:** each attempt takes a transaction-owned application lock on the request's routing key with a bounded wait, then locks the request row; concurrent retries yield exactly one assignment and one transition. Attempts for other requests are never blocked, and a lock that is not granted in time returns 409 instead of failing.
- Request No and `submittedAt` never change.
- The Routing.Recovery role never grants RR-API-004 detail, Approve or Reject.

### 4.7 Approve / Reject (S1-008 — IMPLEMENTED, a45bc38)

**RR-API-006 POST `/api/v1/repair-requests/{id}/approve` — RepairRequest.Review, If-Match, no body**
**RR-API-007 POST `/api/v1/repair-requests/{id}/reject` — RepairRequest.Review, If-Match, body `{ "reason": "string" }`**

- **Who may decide:** only the **assigned approver** of the request's pending approval step (S1-007R `repair_request_approval.assigned_approver_id`), holding APPROVER, within the S1-003 Repair Request scope (tenant + Site). APPROVER + Site alone is not enough.
  - ADMINISTRATOR gains nothing from that role; ADMINISTRATOR + APPROVER follows the same rules.
  - **Self-decision is forbidden:** a user never approves or rejects a request they created, even when holding APPROVER (DEC-PRE-S1-008-01). It is re-checked by the command, not only by routing.
- **Source state:** UNDER_REVIEW only (DEC-PRE-S1-008-03). A SUBMITTED request has no assigned approver yet (not routed, or routing failed) and returns 409 STATE_CONFLICT; so do APPROVED, REJECTED and every other state.
- **Check order and responses:**
  1. 400 missing/invalid If-Match or malformed JSON; 401; 403 ACCESS_DENIED without APPROVER;
  2. 404 NOT_FOUND nonexistent, other tenant or outside the caller's Site scope;
  3. 403 ACCESS_DENIED the caller created the request;
  4. 409 CONCURRENCY_CONFLICT stale ETag, or the request's review lock was not granted in time;
  5. 409 STATE_CONFLICT not UNDER_REVIEW;
  6. 403 ACCESS_DENIED the caller is not the assigned approver of the pending step;
  7. Reject only — 422 VALIDATION_FAILED on `reason`: missing, blank, or longer than 1000 characters after trimming.
- **Success → `200`** with the RR-API-001 Repair Request response shape (`status = APPROVED` or `REJECTED`) and a fresh `ETag`.
- **Written atomically (one transaction):**
  - approval step `status` APPROVED/REJECTED, `decided_at`, `decision_reason` (Reject);
  - Repair Request `status` and `reject_reason` (Reject; trimmed);
  - one audit `REPAIR_REQUEST_APPROVED` / `REPAIR_REQUEST_REJECTED` (from UNDER_REVIEW, to APPROVED/REJECTED, actor = the approver, reason = the reject reason, `approvalId` / `approvalStepNo`, correlation id).
  - No approved_by/approved_at/rejected_by/rejected_at columns exist; decision actor and time are audit data.
  - Any failure writes nothing, and a decided step is final (never re-decided and never removed by routing retry).
- **Concurrency:** the command takes the request's review-workflow lock (the same bounded lock as routing, §4.6) and then checks the ETag, so of two competing decisions exactly one succeeds and the other returns 409 CONCURRENCY_CONFLICT.
- **403 security log:** event `AUTHZ_ACCESS_DENIED` with user, route template and correlation id; no record id, token or header.
- **Not in S1-008:** the EV-SLA-005 SLA stop on Reject and decision notifications (deferred with SLA and notification scope); Return for Correction (RR-API-008); Cancel; Convert; an approval inbox list; the ACTIVE-user check (REQ-FU-USR-001).

### 4.8 Cancel (S1-009 — IMPLEMENTED, 305079f9b8bc2e9373330a9fa5a6a61994a64545)

**RR-API-009 POST `/api/v1/repair-requests/{id}/cancel` — RepairRequest.Draft, If-Match, body `{ "reason": "string" }`**

- **Who may cancel:** only the **owning Requester** (`created_by`) within the S1-003 Repair Request scope (ST-RR-007 actor Requester; UC-RR-004 "Own Request").
  - Approver, Coordinator, Supervisor and Administrator have no Request Cancel permission. Coordinator cancels Visits and Supervisor cancels Work Orders; those are other objects.
  - A multi-role user (e.g. REQUESTER + SUPERVISOR) may cancel only their own requests.
- **Source states:** DRAFT, SUBMITTED, UNDER_REVIEW, APPROVED → **CANCELLED**, which is terminal (no reopen). REJECTED, CANCELLED and CONVERTED are denied.
- **Check order and responses:**
  1. 400 missing/invalid If-Match or malformed JSON; 401; 403 ACCESS_DENIED without REQUESTER;
  2. 404 NOT_FOUND nonexistent, not owned, outside the caller's Site scope, or other tenant;
  3. 409 CONCURRENCY_CONFLICT stale ETag, or the request's review lock was not granted in time;
  4. 409 STATE_CONFLICT REJECTED / CANCELLED / CONVERTED;
  5. 422 VALIDATION_FAILED on `reason`: missing, blank, or longer than 1000 characters after trimming.
- **Success → `200`** with the RR-API-001 Repair Request response shape (`status = CANCELLED`) and a fresh `ETag`.
- **Written atomically:**
  - `status` CANCELLED and `cancel_reason` (RR-DD-001 RR-015, trimmed);
  - one audit `REPAIR_REQUEST_CANCELLED` (from = the source state, to = CANCELLED, reason, actor = the owner, correlation id).
  - Request No and `submittedAt` never change. Any failure writes nothing.
- **Approval rows are left unchanged** (DEC-PRE-S1-009-01): an assigned PENDING step stays PENDING, and decided or routing-failure rows keep their state. After Cancel, Approve/Reject return 409 (not UNDER_REVIEW), Admin Retry returns 409 (not SUBMITTED), and the routing-issue list no longer shows the request.
- **Concurrency:** Cancel takes the same review-workflow lock as routing and Approve/Reject (§4.6, §4.7), then checks the ETag, so exactly one of Cancel / Approve / Reject / routing succeeds and the others return 409.
- **Not in S1-009:** the SLA stop (EV-SLA-005 stop_reason REQUEST_CANCELLED; deferred with `sla_record`); NTF-CANCEL (notification scope); Return for Correction; Convert.

### 4.9 Return for Correction & Resubmit (S1-010 — IMPLEMENTED, 5064c6b05ae22dd443498f9734368eaec61b8437)

**RR-API-008 POST `/api/v1/repair-requests/{id}/return-for-correction` — RepairRequest.Review, If-Match, body `{ "reason": "string" }`**

- **Who may return:** only the **assigned approver** of the current-cycle PENDING approval step, never the request's creator (UC-RR-003; DEC-PRE-S1-008-01; DEC-PRE-S1-010-03).
- **Source state:** UNDER_REVIEW only → **DRAFT**. There is no RETURNED state. SUBMITTED, APPROVED, REJECTED, CANCELLED, CONVERTED and DRAFT return 409 STATE_CONFLICT.
- **Check order and responses** (the same order as Approve/Reject, §4.7):
  1. 400 missing/invalid If-Match or malformed JSON; 401; 403 ACCESS_DENIED without APPROVER;
  2. 404 NOT_FOUND nonexistent, outside the caller's Site scope, or other tenant;
  3. 403 ACCESS_DENIED when the caller created the request;
  4. 409 CONCURRENCY_CONFLICT stale ETag, or the request's review lock was not granted in time;
  5. 409 STATE_CONFLICT not UNDER_REVIEW;
  6. 403 ACCESS_DENIED when the caller is not the assigned approver of the current-cycle PENDING step;
  7. 422 VALIDATION_FAILED on `reason`: missing, blank, or longer than 1000 characters after trimming.
- **Success → `200`** with the RR-API-001 Repair Request response shape (`status = DRAFT`, unchanged `requestNo` and `submittedAt`) and a fresh `ETag`.
- **Written atomically:**
  - the approval step: `status` RETURNED_FOR_CORRECTION, `decision_reason` (APR-008, trimmed), `decided_at`. The step is kept as decision history and is never changed again;
  - the request: `status` DRAFT; no other field changes (no return-reason column exists on `repair_request`);
  - one audit `REPAIR_REQUEST_RETURNED_FOR_CORRECTION` (UNDER_REVIEW → DRAFT, reason, actor = approver, approvalId, approvalStepNo, approvalCycleNo, correlation id).
  - Any failure writes nothing.
- **After Return:** the owner edits the DRAFT with RR-API-002 and may add attachments (FILE-API-001), exactly as for any DRAFT. Cancel (RR-API-009) is also allowed.

**Resubmit — RR-API-005 POST `/api/v1/repair-requests/{id}/submit` on a returned DRAFT**

- Same policy, body, check order and 422 contract as Submit (§4.1), including the CLEAN-photo rule.
- **Kept:** `requestNo`, `submittedBy` and `submittedAt` (SLA start marker; the SLA continues — DEC-PRE-S1-010-02). No Request No is allocated.
- **Duplicates:** the BR-14 check runs again and never matches the request itself. With matches, a `duplicateContinuationReason` is required and replaces the stored one; without matches, the stored reason is kept (DEC-PRE-S1-010-04).
- **Written atomically:** DRAFT → SUBMITTED and one audit `REPAIR_REQUEST_RESUBMITTED` (`requestNo`, original `submittedAt`, `duplicateCount` when > 0, the applied reason, actor = owner; the audit time is the resubmission time).
- **Routing:** System routing runs after the commit exactly as for Submit (§4.6). The route configuration is evaluated again and the step is created in the **next approval cycle**; the returned step of the earlier cycle is not changed (DEC-PRE-S1-010-01). A routing failure stays SUBMITTED, appears in the routing-issue list, and Admin Retry routes it within the new cycle. The response is `UNDER_REVIEW` only when routing assigned an approver.

- **Concurrency:** Return takes the review-workflow lock shared with routing, Approve/Reject and Cancel, then checks the ETag, so exactly one of Return / Approve / Reject / Cancel succeeds. Resubmit uses the Submit duplicate-key lock and the row-version check, so of Resubmit / Cancel / a second Resubmit exactly one succeeds, and only one new approval cycle is routed. UNIQUE(repair_request_id, approval_step_no, approval_cycle_no) is the database backstop.
- **Not in S1-010:** NTF-RETURNED and other notifications; SLA calculation and `sla_record`; multi-step approval; approver reassignment; the approval inbox (it must use the current cycle and the request status); attachment removal; Convert.

## 5. Approved Contract Amendments from the Pre-S1-007 Resolution — IMPLEMENTED (S1-007)

These come from the Pre-S1-007 requirement resolution (RR-DEC-001 §7). **All of them are implemented in S1-007** (commit 36ffc6a). The table is kept as the decision trace; the implemented behaviour is described in §4.1.

| Endpoint | Amendment | Decision |
|---|---|---|
| RR-API-001 / RR-API-002 | Accept and return `requestCategoryCode` and `priorityCode` (tenant-scoped seeded lookup codes) and `requestContactId` (existing user of the same tenant with business scope on the selected Site; the ACTIVE-user check is deferred — DEC-PRE-S1-007-04, REQ-FU-USR-001). `locationId` remains **not accepted** in Sprint 1 | DEC-PRE-S1-007-01, -02, -04, -05; supersedes DEC-S1-005-E1 |
| RR-API-005 POST `/api/v1/repair-requests/{id}/submit` | Body `{ duplicateContinuationReason: string \| null }`; `If-Match` required (catalog, unchanged) | RR-API-001 §4 (baseline) |
| RR-API-005 | Submit validation counts only CLEAN JPG/JPEG/PNG attachments. Without at least one → 422 VALIDATION_FAILED: the request stays DRAFT, `requestNo` stays null, the SLA does not start, and no Submit transition or success audit occurs. PENDING and FAILED attachments do not satisfy mandatory evidence and do not block Submit. They remain unusable; only CLEAN files are downloadable (FILE-API-002), so FAILED files are never downloadable | DEC-PRE-S1-007-06, -07 |
| RR-API-005 | BR-14 match with no continuation reason → **422 VALIDATION_FAILED**, error on `duplicateContinuationReason`, plus a `duplicateCount` only (no identifiers of other requests); request stays DRAFT | DEC-PRE-S1-007-09, -10 |
| RR-API-005 | On success the response carries `requestNo` in format `RR-{yyyy}-{000000}` (per tenant per UTC year), `status = SUBMITTED` (amended by S1-007R: `UNDER_REVIEW` when routing assigns an approver right after the commit — DEC-PRE-S1-007R-01, §4.6), `submittedAt` (the recorded SLA start timestamp), and a fresh ETag. No SLA due, risk or breach fields are returned; that calculation is deferred to the SLA Policy/Monitoring scope. Repeat/stale Submit → 409 | DEC-PRE-S1-007-11, -12 |

**Not decided and not documented as endpoints:**
- Read-only lookup endpoints for Category, Priority and eligible contacts are not implemented in S1-007. They remain an open design item (RR-DEC-001 §9 NB-6), and no such endpoint exists.

## 6. Traceability Summary

| Area | Business Rule / Decision | State | API | Test Case (RR-TC-001) | Automated evidence |
|---|---|---|---|---|---|
| Draft create/edit | FR-01; BR-03/16; D-11; DEC-S1-005-E1..E3 | ST-RR-001 | RR-API-001/002/004 | TC-RR-001; TC-SEC-001/002 | RepairRequestDraftEndpointsTests, RepairRequestDraftServiceTests, RepairRequestDraftStoreTests, RepairRequestDraftTests |
| Attachments | RR-REQ-001 §9 File NFR; DEC-PS1-005; DEC-S1-006-F1..F4, SK, PG | FAS PENDING → CLEAN/FAILED (DEC-PS1-005 optional table) | FILE-API-001/002/003 | TC-RR-002; TC-SEC-001 | AttachmentEndpointsTests, RepairRequestAttachmentServiceTests, FileScanServiceTests |
| Master data | DEC-PS1-001..003/013..015; DEC-S1-004-D1..D4 | ST-CUST/SITE/EQP (ACTIVE/INACTIVE; register-required, not yet authored) | CUST/SITE/EQP-API-* | TC-CUST/SITE/EQP-* (not yet authored) | CustomerEndpointsTests, SiteAndEquipmentEndpointsTests, CustomerServiceTests, SiteAndEquipmentServiceTests, MasterDataEntityTests, PersistenceConstraintTests |
| Authentication | DEC-PS1-004 | — | AUTH-API-001..003 | TC-AUTH-* (not yet authored) | Authentication integration tests |
| Submit (S1-007) | FR-02; BR-01/02/14/16; D-12; RR-DD-001 RR-014/018; DEC-PRE-S1-007-01..12 (ACTIVE contact deferred → REQ-FU-USR-001) | ST-RR-002 (SLA start = `submitted_at`); SLA calculation / EV-SLA-001..005 deferred | RR-API-001/002/004/005 (§4.1, §5) | TC-RR-003/004; TC-SEC-001/002 (TC-SLA-\* deferred) | RepairRequestSubmitEndpointsTests, RepairRequestSubmitServiceTests, RepairRequestSubmitStoreTests, RepairRequestSubmitTests, RequestNumberAndLookupTests, MigrationSeedTests, DevelopmentMalwareScanningTestingTests, DevelopmentMalwareScanningDevelopmentTests, DevelopmentMalwareScanningProductionTests |
| Approval routing (S1-007R) | ST-RR-003; UC-RR-002/003; RR-DD-001 ARC-*/APR-*; DEC-PRE-S1-007R-01..10; DEC-PRE-S1-008-01 (routing portion) | ST-RR-003 SUBMITTED → UNDER_REVIEW (System); failure stays SUBMITTED | RR-API-005 response status (§4.1); ROUTE-API-001..005; ROUTING-API-001/002 (§4.6) | TC-RR-005; TC-SEC-001/002 | ApprovalRoutingEndpointsTests, ApprovalRoutingTests, RepairRequestRoutingServiceTests, ApprovalRouteServiceTests, ApprovalRoutingRulesTests, ApprovalRoutingDomainTests, PersistenceModelTests |
| Return for Correction & Resubmit (S1-010) | UC-RR-002/003; BR-01/14; D-12; RR-DD-001 RR-003/011/014, APR-005 (amended)..010; DEC-PRE-S1-010-01..04 | ST-RR-006 UNDER_REVIEW → DRAFT; ST-RR-002 again; ST-RR-003 into the next approval cycle | RR-API-008; RR-API-005 resubmission (§4.1, §4.9) | TC-RR-008; TC-RR-004; TC-SEC-001 | RepairRequestReturnForCorrectionEndpointsTests, RepairRequestReturnResubmitTests, RepairRequestReturnForCorrectionServiceTests, RepairRequestResubmitServiceTests, RepairRequestReturnForCorrectionDomainTests |
| My Visits / Check-in (S3-001, PR #6) | BR-05; ST-WS-001; ST-SV-002; ST-WO-002; UC-WO-016 (Check-in half only) — "Primary team"/location out of scope, confirmed pre-implementation | Visit/WO SCHEDULED → IN_PROGRESS; Work Session — CHECKED_IN | WS-API-ADD-001; WS-API-001 (§4.11) | Not yet authored in RR-TC-001 | WorkSessionEndpointsTests, WorkSessionDomainTests, WorkOrderCheckInDomainTests |
| Pause Work Session (S3-002, PR #7, commit db6bc5b) | ST-WS-002; UC-WO-012 (Pause half only); RR-DD-001 WS-005/007/010 — reason and per-pause history are Project Owner directives beyond the baseline (§4.12) | Work Session CHECKED_IN → PAUSED; Visit and Work Order unchanged | WS-API-002; WS-API-ADD-002 (§4.12) | Not yet authored in RR-TC-001 | WorkSessionPauseEndpointsTests, WorkSessionPauseDomainTests, WorkSessionStatusTransitionsTests |
| Resume Work Session (S3-003, PR #8, commit 459501a) | UC-WO-012 (Resume half); no Business Rule is scoped to Resume; WS-005/007/008/010 (§4.13) | Work Session PAUSED → CHECKED_IN; Visit and Work Order unchanged; audit `WORK_SESSION_RESUMED` | WS-API-003 (§4.13) | Not yet authored in RR-TC-001 | WorkSessionResumeEndpointsTests, WorkSessionResumeDomainTests, WorkSessionStatusTransitionsTests |
| Check-out Work Session (S3-004, PR #9, commit a5a9bcb) | BR-06 (the status-transition half only; the summary/outcome/evidence half is not implemented — §4.14); UC-WO-016 (Check-out half); WS-009; SV-014 | ST-WS-004 Work Session CHECKED_IN → CHECKED_OUT; ST-SV-003 Visit IN_PROGRESS → COMPLETED; Work Order unchanged; audit `WORK_SESSION_CHECKED_OUT`, `SERVICE_VISIT_COMPLETED` | WS-API-004 (§4.14) | Not yet authored in RR-TC-001 | WorkSessionCheckOutEndpointsTests, WorkSessionCheckOutDomainTests, WorkSessionStatusTransitionsTests |
| Work Summary submit / review (Ticket 1, PR #10, commit cfa09c7) | BR-06 (UC-WO-020 traces BR-06 only — §4.15 Decision 1); UC-WO-020; §4.15 Decisions 2–5 (Technician submits, Team Lead or Supervisor reviews, closed six-value `repairOutcomeCode` allowlist) | ST-WO-003 IN_PROGRESS → AWAITING_SUPERVISOR_REVIEW (Technician); ST-WO-004 AWAITING_SUPERVISOR_REVIEW → AWAITING_CUSTOMER_ACCEPTANCE (Team Lead or Supervisor); audit `WORK_SUMMARY_SUBMITTED`, `WORK_ORDER_SUBMITTED_FOR_ACCEPTANCE` | WSM-API-001 POST `/work-orders/{id}/submit-work-summary`; WSM-API-002 POST `/work-orders/{id}/submit-for-acceptance`; GET `/work-orders/{id}/work-summary` (policy `WorkOrder.ReadWorkSummary`; implemented but not separately described in §4.15) (§4.15) | TC-WO-008 (submit/review); the Check-out evidence gating of TC-WO-007 is not implemented — no evidence table exists | WorkSummaryEndpointsTests, WorkSummaryDomainTests, WorkOrderWorkSummaryDomainTests, WorkOrderStatusTransitionsTests; Angular `work-summary.service.spec.ts`, `work-summary-review.component.spec.ts`, `team-lead-or-supervisor.guard.spec.ts` |
| Customer Accept (Ticket 2, PR #11, commit d4ca55c) | BR-07; UC-WO-021; §4.16 Decisions 1–4 (Acceptance Contact drawn from REQUESTER, active-user check deferred, contact designated through `WSM-API-002`, `acceptanceContactId` in the response); WO-008/009; CAC-004 | ST-WO-005 AWAITING_CUSTOMER_ACCEPTANCE → COMPLETED (exact designated contact); audit `WORK_ORDER_ACCEPTED` | ACC-API-001 POST `/work-orders/{id}/accept`; ACC-API-ADD-001 GET `/work-orders/{id}/eligible-acceptance-contacts` (§4.16) | TC-WO-009 | CustomerAcceptEndpointsTests, WorkOrderWorkSummaryDomainTests, WorkOrderStatusTransitionsTests, AuthorizationPoliciesTests; Angular `work-order-detail.component.spec.ts` (Accept), `work-summary-review.component.spec.ts` (contact designation) |
| Customer Reject and Corrective Action draft (Ticket 3, PR #12, commits eb4eea8 and bff7280) | BR-07/BR-15; UC-WO-022; ST-CA-001; ACC-001..007/009; CA-001/003..012; §4.17 (append-only `customer_acceptance` history for both Accept and Reject, `TenantId` on the new tables, `corrective_action.owner_team_lead_id` nullable) | ST-WO-007 AWAITING_CUSTOMER_ACCEPTANCE → CORRECTIVE_ACTION_REQUIRED; a Corrective Action is created as DRAFT in the same transaction (ST-CA-001); audit `WORK_ORDER_REJECTED`; bff7280 maps a unique-index violation on a concurrent Accept/Reject to `409 CONCURRENCY_CONFLICT` | ACC-API-002 POST `/work-orders/{id}/reject` (`decisionReason` required) (§4.17) | TC-WO-009/010 (TC-WO-010 only partially: plan/approve/rework/resubmit/re-accept are not implemented) | CustomerRejectEndpointsTests, CustomerAcceptEndpointsTests, WorkOrderWorkSummaryDomainTests, WorkOrderStatusTransitionsTests; Angular `work-order-detail.component.spec.ts` (Reject) |
| Cost Summary prepare / edit (Ticket 4a, PR #13, commit c48659f) | BR-08; CST-001/003..011; §4.18 (Team Lead actor per CST-007 and CST-API-001; one row per Work Order; no status enum — `reviewed_at` nullability is the only state signal; baseline fields only; two-phase If-Match) | No Work Order transition (it stays COMPLETED); audit `COST_SUMMARY_PREPARED` (create) and `COST_SUMMARY_UPDATED` (edit) | CST-API-001 PUT `/work-orders/{id}/cost-summary`; `WorkOrderResponse` gains `costSummaryRowVersion` and `costSummaryReviewedAt` (§4.18) | TC-CST-001 (only the Prepare portion) | CostSummaryPrepareEndpointsTests, CostSummaryDomainTests, AuthorizationPoliciesTests; Angular `work-order-detail.component.spec.ts` (Prepare Cost Summary) |
| Cost Summary read / review (Ticket 4b, PR #14, commit 30246d3) | BR-08; CST-001/003..011; §4.19 Decisions 1–5 (Separation of Duties: the reviewer must not be the preparer, even holding both roles; Mark as Reviewed only; no reviewer note; Review requires a COMPLETED Work Order; Supervisor pending-review queue as a technical addition) | No Work Order transition; `reviewed_by` and `reviewed_at` set; filtered index `IX_cost_summary_pending_review`; audit `COST_SUMMARY_REVIEWED` | CST-API-002 POST `/work-orders/{id}/review-cost-summary`; technical additions GET `/work-orders/{id}/cost-summary` and GET `/work-orders/pending-cost-summary-review` (§4.19) | TC-CST-001 (still partial — Review has no baseline test case) | CostSummaryReviewEndpointsTests, CostSummaryDomainTests, AuthorizationPoliciesTests; Angular `cost-summary-review.component.spec.ts`, `supervisor-only.guard.spec.ts`, `work-order-detail.component.spec.ts` (Prepare form pre-fill) |
| Work Order Close (Ticket 5, working tree, not yet committed) | BR-08 (the SLA-stop result of BR-12/EV-SLA-005 is deferred — §4.20 Q1); WO-011/WO-012; §4.20 Q1–Q7; supersedes §4.15 Decision 4 (the full Work Summary + Cost Summary guard is enforced) | ST-WO-006 COMPLETED → CLOSED (Supervisor), sets `closed_by` and `closed_at`; no SLA stop; audit `WORK_ORDER_CLOSED` | WO-API-010 POST `/work-orders/{id}/close`; `WorkOrderResponse` gains `closedAt` (`closedBy` is never exposed) (§4.20) | TC-WO-011; TC-CST-001 (Close portion) | WorkOrderCloseEndpointsTests, WorkOrderCloseServiceTests, WorkOrderCloseDomainTests, WorkOrderStatusTransitionsTests, AuthorizationPoliciesTests; Angular `work-order-detail.component.spec.ts` (Close Work Order) |
