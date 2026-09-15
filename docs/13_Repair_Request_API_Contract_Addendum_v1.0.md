RR-API-001-ADD

# Repair Request — API Contract Addendum (Sprint 1 Implemented Endpoints)

Companion to RR-API-001 v1.2. It documents what is implemented, including the Pre-S1-007 contract amendments implemented in S1-007.

## Document Control

| Field | Value |
|---|---|
| Document ID | RR-API-001-ADD |
| Version | 1.0 |
| Status | Approved for Portfolio Development (documentation reconciliation) |
| Revision Date | 15 September 2026 — reconciled with the S1-007 implementation (Submit, Category/Priority/contact) |
| Extends | RR-API-001 v1.2 (PDF, unchanged) |
| Decision source | RR-DEC-001 v1.2 (`12_Pre_Sprint1_Baseline_Decision_Register_v1.0.md`) |
| Implementation evidence | S1-002 (d2e96c5), S1-003 (7a4be91), S1-004 (c03c4c4), S1-005 (46f12a6), S1-006 (ce69f6e), S1-007 (uncommitted; commit hash to be recorded at commit) |
| Approval | Portfolio Project Owner Approval (DEC-PS1-016) |

**Rules for this addendum:**
- The RR-API-001 PDF is not modified.
- An endpoint is listed as implemented only if a controller route exists in the S1-007 codebase.
- Addendum IDs (`AUTH-API-*`, `CUST-API-*`, `SITE-API-*`, `EQP-API-*`, `FILE-API-003`, `SYS-API-*`) are documentation numbers, not new business semantics.
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
| RR-API-005 | POST `/api/v1/repair-requests/{id}/submit` | IMPLEMENTED (S1-007) — see §4.1 |
| RR-API-006..010 | approve / reject / return / cancel / convert | NOT IMPLEMENTED |
| FILE-API-001 | POST `/api/v1/repair-requests/{id}/attachments` | IMPLEMENTED (S1-006) |
| FILE-API-002 | GET `/api/v1/files/{fileAssetId}` | IMPLEMENTED (S1-006) |
| WO-*, WS-*, ACC-*, CA-*, CST-*, TIME-*, SLA-*, AUD-*, REP-*, NTF-* | — | NOT IMPLEMENTED (later sprints / tickets) |

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

---

## 3. Common Conventions (as implemented)

### 3.1 Authorization policies

| Policy | Roles | Source |
|---|---|---|
| MasterData.Read | All approved roles | RR-REQ-001 §3/§13; S1-003 |
| MasterData.Manage | ADMINISTRATOR | RR-REQ-001 §3 ("Master data ... configuration") |
| RepairRequest.Read | REQUESTER, APPROVER, COORDINATOR, TECHNICIAN, TEAM_LEAD, SUPERVISOR | FR-09; S1-003 |
| RepairRequest.Draft | REQUESTER | RR-REQ-001 §13 |

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
- **Success → `200`** with the RR-API-001 response shape, `status = SUBMITTED`, `requestNo` `RR-{yyyy}-{000000}` (per tenant, per UTC year), `submittedAt` (the SLA start marker only), and a fresh `ETag`.
  - No SLA due, risk or breach fields; no routing or notification (DEC-PRE-S1-007-11/-12).
- **Atomicity:** Request No allocation (per tenant-year counter, never MAX + 1), the DRAFT → SUBMITTED transition and the audit `REPAIR_REQUEST_SUBMITTED` (from/to state, `requestNo`, `submittedAt`, `duplicateCount`, reason) commit in one transaction.
- **Concurrent or deadlocked Submit** of the same request → 409; exactly one succeeds.
- **Concurrent Submits of different Drafts with the same duplicate key** are serialized per key inside the Submit transaction (RR-DEC-001 DEC-PRE-S1-007-09, implementation note).
  - At most one of them proceeds without a continuation reason; the other receives the duplicate warning (422 with `duplicateCount`) and stays DRAFT.
  - Submits with other keys are not delayed by this.
  - If the key cannot be locked within the wait limit → 409 CONCURRENCY_CONFLICT, and nothing is written.
- **Any failed Submit** writes nothing: no Request No, no `submittedAt`, no audit.
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

## 5. Approved Contract Amendments from the Pre-S1-007 Resolution — IMPLEMENTED (S1-007)

These come from the Pre-S1-007 requirement resolution (RR-DEC-001 §7). **All of them are implemented in S1-007** (uncommitted at the time of this revision). The table is kept as the decision trace; the implemented behaviour is described in §4.1.

| Endpoint | Amendment | Decision |
|---|---|---|
| RR-API-001 / RR-API-002 | Accept and return `requestCategoryCode` and `priorityCode` (tenant-scoped seeded lookup codes) and `requestContactId` (existing user of the same tenant with business scope on the selected Site; the ACTIVE-user check is deferred — DEC-PRE-S1-007-04, REQ-FU-USR-001). `locationId` remains **not accepted** in Sprint 1 | DEC-PRE-S1-007-01, -02, -04, -05; supersedes DEC-S1-005-E1 |
| RR-API-005 POST `/api/v1/repair-requests/{id}/submit` | Body `{ duplicateContinuationReason: string \| null }`; `If-Match` required (catalog, unchanged) | RR-API-001 §4 (baseline) |
| RR-API-005 | Submit validation counts only CLEAN JPG/JPEG/PNG attachments. Without at least one → 422 VALIDATION_FAILED: the request stays DRAFT, `requestNo` stays null, the SLA does not start, and no Submit transition or success audit occurs. PENDING and FAILED attachments do not satisfy mandatory evidence and do not block Submit. They remain unusable; only CLEAN files are downloadable (FILE-API-002), so FAILED files are never downloadable | DEC-PRE-S1-007-06, -07 |
| RR-API-005 | BR-14 match with no continuation reason → **422 VALIDATION_FAILED**, error on `duplicateContinuationReason`, plus a `duplicateCount` only (no identifiers of other requests); request stays DRAFT | DEC-PRE-S1-007-09, -10 |
| RR-API-005 | On success the response carries `requestNo` in format `RR-{yyyy}-{000000}` (per tenant per UTC year), `status = SUBMITTED`, `submittedAt` (the recorded SLA start timestamp), and a fresh ETag. No SLA due, risk or breach fields are returned; that calculation is deferred to the SLA Policy/Monitoring scope. Repeat/stale Submit → 409 | DEC-PRE-S1-007-11, -12 |

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
