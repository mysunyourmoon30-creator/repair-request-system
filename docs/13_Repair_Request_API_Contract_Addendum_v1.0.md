RR-API-001-ADD

# Repair Request — API Contract Addendum (Sprint 1 Implemented Endpoints)

Companion to RR-API-001 v1.2. It documents what is implemented, including the Pre-S1-007 contract amendments implemented in S1-007.

## Document Control

| Field | Value |
|---|---|
| Document ID | RR-API-001-ADD |
| Version | 1.0 |
| Status | Approved for Portfolio Development (documentation reconciliation) |
| Revision Date | 24 September 2026 — Sprint 4 round decisions recorded ahead of implementation (Work Summary Submit/Review, Customer Accept/Reject, Work Order Close; no code exists yet — see §4.15); previously 23 September 2026 (S3-004 Check-out Work Session; working tree, not yet committed), 22 September 2026 (S3-003, PR #8, commit 459501a), 19 September 2026 (S3-002, PR #7, commit db6bc5b), 18 September 2026 (S3-001) and 17 September 2026 (S2-001) |
| Extends | RR-API-001 v1.2 (PDF, unchanged) |
| Decision source | RR-DEC-001 v1.2 (`12_Pre_Sprint1_Baseline_Decision_Register_v1.0.md`), Section 11 for S2-001; Portfolio Project Owner pre-implementation directives for S3-001 (eligibility is `assigned_technician_id` only — no "Primary team"; location capture out of scope) for My Visits / Check-in; Portfolio Project Owner scope decision for S3-004 (Check-out is a pure status transition — BR-06's summary/outcome/evidence half is a later ticket); Portfolio Project Owner directives for the Sprint 4 round (UC-WO-020 traceability correction, Technician/Team-Lead-Supervisor/Customer-Acceptance-Contact role split, WO Close guard trimmed — see §4.15) |
| Implementation evidence | S1-002 (d2e96c5), S1-003 (7a4be91), S1-004 (c03c4c4), S1-005 (46f12a6), S1-006 (ce69f6e), S1-007 (36ffc6a), S1-007R (b1cad73), S1-008 (a45bc38), S1-009 (305079f), S1-010 (5064c6b), S2-001 (cdfc0a8), S2-003 Schedule/Service Visit management (9a05ca0, PR #5) — WO-API-002..007 implemented but **not yet reconciled into this addendum's §1/§4** (pre-existing gap, out of scope of this revision), S3-001 My Visits / Check-in (ee115a8, PR #6 — see §4.11), S3-002 Pause Work Session (db6bc5b, PR #7 — see §4.12), S3-003 Resume Work Session (459501a, PR #8 — see §4.13), S3-004 Check-out Work Session (working tree — see §4.14) |
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
| WS-API-004 | POST `/api/v1/work-sessions/{id}/check-out` | IMPLEMENTED (S3-004, working tree) — Technician-only, own CHECKED_IN session only, no body, no summary/outcome/evidence (Portfolio Project Owner scope decision); see §4.14 |
| ACC-*, CA-*, CST-*, TIME-*, SLA-*, AUD-*, REP-*, NTF-*, WO-API-008..011 | — | NOT IMPLEMENTED (later sprints / tickets) |

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

Convert (WO-API-010's creation counterpart, `ST-RR-008`/`UC-WO-001`), Schedule, Reassign and every other Work Order mutation are **not implemented** — `work_order` is empty in production until Convert exists. Automated tests seed `RepairRequest`/`WorkOrder` rows directly through EF Core.

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

### 4.12 S3-002 Pause Work Session (working tree, not yet committed)

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

### 4.14 S3-004 Check-out Work Session (working tree, not yet committed)

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

### 4.15 Sprint 4 Round — Work Summary Submit/Review, Customer Accept/Reject, Work Order Close (decisions recorded ahead of implementation; no code exists yet)

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
BR-12 and ST-WO-006's printed guard is *"Work Summary + Cost Summary reviewed"*. Cost Summary (CST-*, CST-API-*) is not built this round. Per Portfolio Project Owner directive, **Close's guard is trimmed to "Work Summary reviewed" only** for this round; the Cost Summary half of BR-12's guard is deferred to a later ticket — mirroring §4.14's BR-06 Check-out split exactly (baseline ties two things together, one deferred, the deferral documented here rather than silently dropped).

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

**Not in this round:** Cost Summary (BR-12's other half — a later ticket); Corrective Action's plan/approval/rework cycle (UC-WO-023/024 — Reject is still planned to create a minimal `CorrectiveAction` row in `DRAFT` status atomically, per ST-WO-007's own documented side effect, but nothing beyond that DRAFT row); Cancel Work Order (UC-WO-026); Time Correction (preserved on its own holding branch `feature/s6-time-correction-pending-scope`, untouched by this round).

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

- Every non-anonymous endpoint is deny-by-default and needs an authenticated principal (JWT bearer, DEC-PS1-004).
- Tenant, actor and scope are always derived on the server (D-11 / BR-16). Unknown JSON members such as `tenantId`, `customerId` or `status` are ignored.

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
| Pause Work Session (S3-002, working tree) | ST-WS-002; UC-WO-012 (Pause half only); RR-DD-001 WS-005/007/010 — reason and per-pause history are Project Owner directives beyond the baseline (§4.12) | Work Session CHECKED_IN → PAUSED; Visit and Work Order unchanged | WS-API-002; WS-API-ADD-002 (§4.12) | Not yet authored in RR-TC-001 | WorkSessionPauseEndpointsTests, WorkSessionPauseDomainTests, WorkSessionStatusTransitionsTests |
