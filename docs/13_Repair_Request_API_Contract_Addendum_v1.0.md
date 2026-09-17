RR-API-001-ADD

# Repair Request — API Contract Addendum (Sprint 1 Implemented Endpoints)

Companion to RR-API-001 v1.2. It documents what is implemented, including the Pre-S1-007 contract amendments implemented in S1-007.

## Document Control

| Field | Value |
|---|---|
| Document ID | RR-API-001-ADD |
| Version | 1.0 |
| Status | Approved for Portfolio Development (documentation reconciliation) |
| Revision Date | 17 September 2026 — reconciled with the S2-001 implementation (Work Order List/Detail); previously 15 September 2026 (S1-007) |
| Extends | RR-API-001 v1.2 (PDF, unchanged) |
| Decision source | RR-DEC-001 v1.2 (`12_Pre_Sprint1_Baseline_Decision_Register_v1.0.md`), Section 11 for S2-001 |
| Implementation evidence | S1-002 (d2e96c5), S1-003 (7a4be91), S1-004 (c03c4c4), S1-005 (46f12a6), S1-006 (ce69f6e), S1-007 (36ffc6a), S1-007R (b1cad73), S1-008 (a45bc38), S1-009 (305079f), S1-010 (5064c6b), S2-001 (pending commit hash — recorded once merged, per the established Sprint 1 pattern) |
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
| WO-API-001 | GET `/api/v1/work-orders/{id}` | IMPLEMENTED (S2-001) — read-only List/Detail; Convert/Schedule/Reassign not implemented, see §4.10 |
| WS-*, ACC-*, CA-*, CST-*, TIME-*, SLA-*, AUD-*, REP-*, NTF-*, WO-API-002..011 | — | NOT IMPLEMENTED (later sprints / tickets) |

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

---

## 4.10 S2-001 Work Order List/Detail

Read-only. `WorkOrder.Read` allows REQUESTER, APPROVER, COORDINATOR, TEAM_LEAD, SUPERVISOR — TECHNICIAN and ADMINISTRATOR are denied `403 ACCESS_DENIED` outright (DEC-S2-001-03). Row-level scope mirrors `RepairRequest.Read`/`IDataScope.RepairRequests`, correlated through the Work Order's `repair_request_id` (site-wide roles see Work Orders whose linked Repair Request's Site is in the caller's `user_site_scope`; REQUESTER sees only Work Orders of Repair Requests it created); a nonexistent and an out-of-scope id return the same `404 NOT_FOUND` body (WO-API-001 detail).

Response fields are `workOrderId, workOrderNo, status, repairRequestId, repairRequestNo, customerCode, siteCode, equipmentCode, rowVersion` — reused unchanged for both the list item and the detail response. `customerCode`/`siteCode`/`equipmentCode` are codes, not display names (DEC-S2-001-01). Scheduled Date and Assigned Team/Technician/Team Lead are never returned (DEC-S2-001-02/03); `owner_team_id`/`team_lead_id` are persisted on `work_order` but not projected into the response.

`GET /api/v1/work-orders` query string: `page`, `pageSize` (existing `Paging:DefaultPageSize`/`MaxPageSize` convention) and an optional `status` (one of the `WorkOrderStatus` codes; an unrecognised value is `400 BAD_REQUEST`). Sort is fixed newest-first via a technical `created_at` column added to `work_order` for this purpose only (not one of the RR-DD-001 WO-001..013 business fields, DEC-S2-001-04); there is no client-selectable sort in this ticket.

Convert (WO-API-010's creation counterpart, `ST-RR-008`/`UC-WO-001`), Schedule, Reassign and every other Work Order mutation are **not implemented** — `work_order` is empty in production until Convert exists. Automated tests seed `RepairRequest`/`WorkOrder` rows directly through EF Core.

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
