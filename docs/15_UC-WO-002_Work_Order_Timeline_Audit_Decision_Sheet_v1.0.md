RR-DEC-UC-WO-002

# Repair Request — UC-WO-002 / AUD-API-001 "Work Order Timeline (Audit Read)" Decision Sheet

Cross-reference of `docs/01`–`docs/11`, `docs/12`, `docs/13`, `docs/14` and the current implementation against the UC-WO-002 timeline / AUD-API-001 / FR-09 auditability requirement, with the Portfolio Project Owner decisions D1–D12 that close every open question before implementation starts.

## Document Control

| Field | Value |
|---|---|
| Document ID | RR-DEC-UC-WO-002 |
| Version | 1.1 |
| Status | Decisions approved (D1–D12); Phase 0 corrected in v1.1; Phase 1A spike passed (§11); Phase 1B implementation (§12) and Phase 2 performance acceptance and pre-commit audit (§13) recorded; **committed (`ad4b3c076ea5b5b405a878d451fe3f565c17f280`) and merged to `main` through PR #22 at merge commit `9eb05a7e5fbc41ffa569b931a18ca4b33dbbd91b`; AUD-API-001 is present on `main`** (status correction, 11 October 2026) |
| Revision Date | 10 October 2026 (status correction 11 October 2026) |
| Baseline commit | `39612793df1b7f12a9f8d9d0efb65cb213d4e873` (`main`, merge of PR #21) |
| Extends / cross-references | RR-REQ-001 (`01_...pdf`), BR-RR-BASELINE (`02_...pdf`), UC-RR-001 v1.7 (`04_...pdf`), RR-DD-001 (`05_...pdf`), RR-TC-001 (`06_...pdf`), RR-DBD-001 v1.2 (`08_...pdf`), RR-API-001 v1.2 (`09_...pdf`), RR-UI-001 (`10_...pdf`), RR-PERF-001 (`11_...pdf`), RR-ARCH-001, RR-DEC-001 v1.2 (`12_...md`), RR-API-001-ADD v1.0 (`13_...md`), RR-DEC-S2-001 (`14_...md`) |
| Approval | Portfolio Project Owner Approval — D1–D12 (7 October 2026); Phase 0 review corrections 1–2 applied in v1.1 (7 October 2026), D1–D12 unchanged |
| Revision history | v1.0 — Phase 0 sheet. v1.1 — (1) the validation order is now 401, 403, 400 (pageSize/cursor syntax), 400 (cursor HMAC/version/binding), 404 (unsupported entity type / scoped lookup); the HMAC is verified from the server context and the route before any Work Order lookup; the earlier claim that checking the signature after the scope check prevents existence disclosure is withdrawn (§4, §5, §6, §8); (2) the "about 5 × (pageSize + 1) rows" read-volume statement is withdrawn and replaced by the index/ordering facts, a prohibition on client-side Guid comparison or pagination, an executable Phase 1 spike with a stop rule, and a mandatory 150-row same-timestamp test (§5, §7, §8, §8.1). Status correction (11 October 2026, documentation only, Sprint 5 Ticket 2 / `docs/16`): the Status row and the Scope note now record that the implementation was committed and merged to `main` through PR #22 (merge commit `9eb05a7e5fbc41ffa569b931a18ca4b33dbbd91b`); no decision (D1–D12), contract, evidence or measurement was changed. |
| Audience | Portfolio Project Owner, SA, Backend/Frontend Developer, Tester |
| Purpose | Fix the contract, security/PII rules, cursor design, performance profile and acceptance criteria for the Work Order timeline, without guessing any business rule the baseline does not state. |

**Revision rule (same as RR-DEC-001 / RR-DEC-S2-001):** This document does not change any locked business semantics. No PDF baseline document (`docs/01`–`docs/11`) is modified. Items the baseline states are marked *Baseline*; items fixed by the owner on top of it are marked *Owner decision* and are recorded here, not attributed to `/docs`. PDF page numbers are physical page numbers of the file (they can differ from the printed "Page x / y" footers).

**Scope note (historical, Phase 0 — superseded):** Phase 0 only. No production code, migration, test, commit or PR exists for this ticket. "V1" below means this ticket's delivery increment. *Status correction (11 October 2026):* this ticket has since been implemented, committed (`ad4b3c076ea5b5b405a878d451fe3f565c17f280`) and merged to `main` through PR #22 at merge commit `9eb05a7e5fbc41ffa569b931a18ca4b33dbbd91b`; the wording "working tree", "uncommitted" and "nothing committed" in §11–§13 describes the state at the date each phase was recorded and is historical. "V1" in this sheet means the timeline ticket's delivery increment, not the product-level V1 defined in `docs/16`.

---

## 1. Baseline evidence

| # | Statement | Source |
|---|---|---|
| B1 | UC-WO-002 "View / Track Work Order, Timeline and Audit": Primary Actor *Authorized user*; Trigger *open list/detail/timeline*; State Flow *no state change*; Preconditions *within tenant/site/role/ownership scope*; Main Flow *search/list/detail/timeline with server-side filters/paging*; Exception *out-of-scope guessed ID -> non-leaking deny/not found*; Validation *backend scope authority*; Audit *read access may security-audit sensitive access*; Postcondition *read-only view*; Traceability FR-09, BR-02/16 | `docs/04` p.6 |
| B2 | AUD-API-001: `GET /api/v1/entities/{entityType}/{entityId}/timeline`, Purpose *Timeline*, Actor *Authorized*, Concurrency *none* | `docs/09` p.5 |
| B3 | FR-09: *"Authorized users search/list/view timeline/audit/report/export within tenant/site/…"*; first-release criterion: *"search/timeline/report/export are tenant/site scoped and prevent sensitive leakage"* | `docs/01` p.4, p.6 |
| B4 | `audit_history` is append-only; fields AUD-001, AUD-003..AUD-013 (entity type/id, action, from/to state, old/new JSON, reason, actor, `occurred_at` immutable UTC, correlation); the business API cannot physically delete audit history (AUD-002 is not present in the extracted dictionary; the code comment on `AuditHistory` records the same and adds `tenant_id`) | `docs/05` p.3, p.10; `docs/08` p.4 |
| B5 | Audit timeline access path: `tenant_id + entity_type + entity_id + occurred_at DESC`; index strategy is a candidate, not a command — capture generated SQL and the actual execution plan / Query Store evidence for the audit timeline before adding anything (§10.2) | `docs/08` p.5, p.6 |
| B6 | UI-031 Work Order Detail has an "…Acceptance/Timeline" section; UI-100 "Timeline / Audit", role *Authorized*, "Read-only audit/timeline"; "Audit: read-only timeline; no edit/delete"; "IDOR: every deep link handles non-leaking 403/404"; "PII: acceptance contact email/phone only to authorized role" | `docs/10` p.3–5 |
| B7 | Error contract: 401 `UNAUTHENTICATED`, 403/404 `ACCESS_DENIED`/`NOT_FOUND` *non-leaking*, 400 `BAD_REQUEST` malformed, 500 `INTERNAL_ERROR` with correlation id only; list/report APIs apply tenant/site scope server-side *before paging*; `page`/`pageSize`/`sort` are technical conventions with the maximum page size being configuration; ISO-8601 UTC, the UI uses the Site time zone | `docs/09` §1–§2 (p.3), p.6 |
| B8 | Performance: server-side scope/filter/sort/paging, focused projection, no full aggregate graph, bounded query inputs (TC-PERF-001/002); focused projections; `CancellationToken` propagated; p95 ≤ 3.0 s is the *recommendation* under PERF-DEC-01 (pending approval); dataset: 100 concurrent users, 100,000 requests per Site, related audit data, ≥ 2 tenants and ≥ 2 Sites, cross-scope negative traffic measured separately; default ramp 5 min / 30 min steady / 5 min | `docs/04` p.16; `docs/09` p.6–7; `docs/06` p.8; `docs/11` p.2 |
| B9 | Architecture: audit history is the business/legal trace (who, what, state before/after, reason, old/new, correlation), append-only; metrics/logs carry no sensitive payload; no Restricted PII, file URLs or tokens in client storage | `RR-ARCH-001` p.6, p.8, p.9 |
| B10 | Test case: *view/track scope — users in/out of scope; authorized visible, out-of-scope denied without leakage; security correlation* | `docs/06` p.4 |

**Not defined by any baseline document** (hence the decisions below): the set of "Authorized" roles for AUD-API-001; which entities compose a Work Order timeline; whether before/after values are shown; how actors are displayed; event labels; the paging style, default/maximum page size and filters for the timeline; whether timeline reads are security-audited.

---

## 2. Current implementation facts (as of the baseline commit)

| # | Fact | Evidence |
|---|---|---|
| F1 | `audit_history` entity has no mutators; `tenant_id` is carried for the timeline access path; `occurred_at` is `datetime2(3)`; `old/new_value_json` are `nvarchar(max)` with an `ISJSON` check | `Domain/Auditing/AuditHistory.cs`; `Infrastructure/Persistence/Configurations/AuditHistoryConfiguration.cs`; model snapshot |
| F2 | The index `IX_audit_history_timeline (tenant_id, entity_type, entity_id, occurred_at DESC)` already exists in the model, the snapshot and migration `20260913133250` | same configuration; `Migrations/RepairRequestDbContextModelSnapshot.cs` |
| F3 | No application code reads audit rows for display. The only reads are routing-failure detail in `ApprovalRoutingStore`. No audit read API, policy, DTO or UI exists | `grep` of `_db.AuditHistory` reads |
| F4 | Work Order history is split across five `entity_type` values: `WORK_ORDER`, `SERVICE_VISIT`, `WORK_SESSION` (linked only through `service_visit_id`), `CORRECTIVE_ACTION` (linked by `work_order_id`), and `REPAIR_REQUEST` (convert event, Work Order id/no only inside `new_value_json`). There is no `work_order_id` on `audit_history` | `Application/WorkOrders/WorkOrderAudit.cs`; `RepairRequests/RepairRequestAudit.cs` |
| F5 | Child lookup indexes already exist: `IX_service_visit_work_order_id`, `IX_work_session_service_visit_id`, `(work_order_id, cycle_no)` on `corrective_action` | `Infrastructure/Persistence/Configurations/*` |
| F6 | `new_value_json` of existing rows contains sensitive data: cost `totalAmount`/`currencyCode`, `acceptanceContactSnapshot`; `reason` is free text. Cost reads are restricted to Team Lead/Supervisor (`CostSummary.Read`) | `WorkOrderAudit.cs`; `AuthorizationPolicies.cs` |
| F7 | Paired rows exist for one user action (check-in: `SERVICE_VISIT_CHECKED_IN` + `WORK_ORDER_STARTED`; check-out: `WORK_SESSION_CHECKED_OUT` + `SERVICE_VISIT_COMPLETED`); cascaded rows (Work Order cancel + Visit cancels) share one `occurred_at`; `from/to_state` vocabularies differ per entity type; some actions carry no JSON | `WorkOrderAudit.cs` |
| F8 | Actors are GUIDs. `ApplicationUser` has only Identity `UserName`/`Email` and `TenantId` (no display name). System actors are constants in `SystemActors` (`ApprovalRouting`, `MalwareScanner`) | `Infrastructure/Identity/ApplicationUser.cs`; `Application/Common/SystemActors.cs` |
| F9 | `WorkOrder.Read` = REQUESTER (own only), APPROVER, COORDINATOR, TEAM_LEAD, SUPERVISOR; TECHNICIAN and ADMINISTRATOR get 403; scope is `IDataScope.WorkOrders` (DEC-S2-001-03) | `AuthorizationPolicies.cs`; `docs/12` §11 |
| F10 | Paging convention: `pageSize` default 20, maximum 100 (`Paging` options), a value below 1 is `400`, above the maximum is clamped; `PagedResponse` is offset-based | `Api/Http/PagingOptions.cs`; `appsettings.json` |
| F11 | The Jwt signing key (`Jwt:SigningKey`, Base64, ≥ 32 bytes) is already validated at startup | `Infrastructure/Authentication/AuthenticationOptionsValidators.cs` |
| F12 | The Site has no time zone in the model; the UI shows raw ISO strings today | code search |
| F13 | Failed commands are not audited (by design); an attachment download is the only audited read | `WorkOrderAudit.cs`; `AttachmentAudit.cs` |
| F14 | `docs/13` is stale for this area: §1 still lists RR-API-003, RR-API-010, CA-* and WO-API-011 as NOT IMPLEMENTED and Close as "working tree"; the header is "Sprint 1 Implemented Endpoints"; §3.1 and §6 have no audit-timeline rows | `docs/13` §1 (lines 29–53) |
| F15 | The Angular Work Order Detail is a single OnPush standalone component; it has no timeline; `getCurrentUserRoles()` is a UX helper only | `frontend/.../work-order-detail.component.ts` |
| F16 | `k6` is not installed on the development machine | `k6 version` |

---

## 3. Owner decisions D1–D12 (approved)

| ID | Decision |
|---|---|
| **D1 Endpoint** | Use the baseline endpoint `GET /api/v1/entities/{entityType}/{entityId}/timeline`. V1 accepts `entityType = WORK_ORDER` only; any other `entityType` returns the same non-leaking `404 NOT_FOUND`. |
| **D2 Composition** | One timeline merges: `WORK_ORDER` rows of the Work Order; `SERVICE_VISIT` rows of its Visits; `WORK_SESSION` rows under those Visits; `CORRECTIVE_ACTION` rows of the Work Order; and **only** the `REPAIR_REQUEST_CONVERTED` row of the originating Repair Request. All child ids are derived on the server from the scoped Work Order; none is accepted from the client. |
| **D3 Readers** | Same roles and scope as `WorkOrder.Read`: REQUESTER (own Work Orders only), APPROVER, COORDINATOR, TEAM_LEAD, SUPERVISOR. TECHNICIAN and ADMINISTRATOR → `403`. Out-of-scope, missing and non-owner → one identical `404`. |
| **D4 Payload / redaction** | Action-specific *allowlisted* `details` only. `old_value_json` / `new_value_json` are never sent. Never sent: `totalAmount`/`currencyCode`, `acceptanceContactSnapshot`/email/phone, notes or any free-text reason, Work Summary text, Corrective Action plan text, or any payload key outside the allowlist. **The V1 DTO has no raw `reason` field.** A future reason disclosure needs its own role-aware decision. |
| **D5 Actor** | Send `actorId` and `kind` ∈ `USER`/`SYSTEM`/`UNKNOWN`. No email or display-name lookup. System actors are `SYSTEM`; an actor that cannot be resolved is `UNKNOWN`. |
| **D6 Pagination** | Keyset/cursor pagination, not offset. Order `occurredAt DESC, auditHistoryId DESC`. The cursor encodes the (`occurredAt`, `auditHistoryId`) pair. Default `pageSize` 20, maximum 100. Response: `items`, `nextCursor`, `hasMore`. No filters in V1. A malformed cursor → `400`. A cursor must not be usable across Work Orders, tenants or sites. The Angular "Load more" additionally de-duplicates by `auditId`. |
| **D7 Entity type** | `WORK_ORDER` only in V1. |
| **D8 Read audit** | Reading a timeline writes no audit row; a GET never changes the number of audit rows. |
| **D9 Time** | The API sends UTC. Angular shows browser-local time; `<time datetime>` keeps UTC and `title` shows UTC. No Site time-zone model is created. |
| **D10 Correlation** | `correlationId` is not sent in V1. Paired events are shown as separate items. |
| **D11 Performance** | Target p95 ≤ 3.0 s under a recorded test profile. `k6`, 100 virtual users. Record p50/p95/p99, throughput and 5xx. Cross-scope negative traffic is measured separately. This round records evidence only — it is **not** a mandatory CI gate. Generated SQL and the execution plan must be captured before any new index is proposed. |
| **D12 Data model** | No schema change and no migration. A composite read model lives in the Application/Infrastructure query layer. No `work_order_id` is added to `audit_history` and nothing is backfilled. The existing index is used first; a separate index is proposed later only if the plan shows a problem. |

---

## 4. Endpoint contract (V1)

**Request:** `GET /api/v1/entities/WORK_ORDER/{entityId}/timeline?pageSize={1..}&cursor={opaque}`

| Item | Rule |
|---|---|
| Authentication / role | JWT bearer required (`401`). New policy `Audit.TimelineRead` with the same role set as `WorkOrder.Read`; other roles `403 ACCESS_DENIED` (D3). |
| Route | `entityType` is a string segment and is **normalized** by invariant upper-casing before any comparison or use in the cursor binding; `entityId` is a `guid` route constraint (a non-GUID id does not match the route and receives the framework's own `404`, as in `docs/13` §3.5, before authentication or authorization is evaluated; that response is data-independent and discloses nothing). |
| Scope | Only **after** a supplied cursor has been accepted, the Work Order is loaded through `IDataScope.WorkOrders(user)` (tenant, Site, REQUESTER ownership). An unsupported normalized `entityType`, a missing Work Order, another tenant, another Site and a non-owner REQUESTER all produce the **same** `404 NOT_FOUND` body. |
| Concurrency | None: a read, no `If-Match`, no `ETag`. `Cache-Control: no-store`. |
| Method set | Only `GET`. Other verbs return `405`. |
| Page size | `pageSize` default 20; below 1 → `400`; above 100 → clamped to 100 (existing `PageRequests` convention, `Paging` options). There is no `page` parameter. |
| Filters / sort | None in V1; the order is fixed (D6). |
| Check order | 1. `401` — no or unusable authentication. 2. `403` — authenticated, but the role lacks `Audit.TimelineRead`. 3. `400` — `pageSize` or cursor *syntax* invalid. 4. `400` — cursor HMAC, version or binding invalid. 5. `404` — unsupported entity type, or the scoped Work Order lookup finds nothing. Steps 3–4 apply to the cursor only when one is supplied. See §5 *Validation order and disclosure*. |

**Response `200`:**

```json
{
  "items": [
    {
      "auditId": "guid",
      "occurredAt": "2026-10-07T03:21:45.123Z",
      "actionCode": "WORK_ORDER_CANCELLED",
      "entityType": "WORK_ORDER",
      "entityId": "guid",
      "fromState": "IN_PROGRESS",
      "toState": "CANCELLED",
      "actor": { "actorId": "guid", "kind": "USER" },
      "details": { "serviceVisitId": "guid" }
    }
  ],
  "nextCursor": "opaque-string-or-null",
  "hasMore": false
}
```

`nextCursor` is `null` exactly when `hasMore` is `false`. `fromState`/`toState` are state codes (not free text) and may be `null`. `details` is always an object (possibly empty). There is no `reason`, `correlationId`, `oldValue`, `newValue`, email or display name.

### 4.1 `details` allowlist (D4)

The projection maps an `actionCode` to a fixed set of JSON keys; every other key is dropped. Only scalar values are copied (string ≤ 64 characters, number, boolean, GUID, ISO date); nested objects/arrays are dropped. A row whose JSON is missing or does not parse yields empty `details` (never an error, never the raw text). An unknown `actionCode` is still returned, with empty `details`.

| actionCode | Allowed `details` keys |
|---|---|
| `REPAIR_REQUEST_CONVERTED` | `workOrderId`, `workOrderNo` |
| `WORK_ORDER_SCHEDULED` | `serviceVisitId`, `ownerTeamId`, `assignedTechnicianId`, `scheduledStartAt`, `scheduledEndAt` |
| `SERVICE_VISIT_CHECKED_IN`, `SERVICE_VISIT_COMPLETED` | `workSessionId` |
| `WORK_SESSION_PAUSED` | `workSessionPauseId`, `pausedAt` |
| `WORK_SESSION_RESUMED` | `workSessionPauseId`, `resumedAt` |
| `WORK_SUMMARY_SUBMITTED` | `workSummaryId`, `repairOutcomeCode` |
| `WORK_ORDER_REJECTED` | `customerAcceptanceId`, `correctiveActionId` |
| `COST_SUMMARY_PREPARED`, `COST_SUMMARY_UPDATED`, `COST_SUMMARY_REVIEWED`, `WORK_ORDER_CLOSED` | `costSummaryId` (never `totalAmount`, `currencyCode`) |
| `CORRECTIVE_ACTION_REWORK_SCHEDULED` | `serviceVisitId`, `assignedTeamId`, `assignedTechnicianId`, `scheduledStartAt`, `scheduledEndAt` |
| `SERVICE_VISIT_RESCHEDULED` | `scheduledStartAt`, `scheduledEndAt` |
| `SERVICE_VISIT_REASSIGNED` | `assignedTeamId`, `assignedTechnicianId` |
| `SERVICE_VISIT_MISSED_DECIDED` | `decision`, `newServiceVisitId` |
| `WORK_ORDER_SUBMITTED_FOR_ACCEPTANCE` | none (`acceptanceContactId` and the snapshot are not exposed) |
| all other codes (`WORK_ORDER_STARTED`, `WORK_ORDER_REWORK_STARTED`, `WORK_SESSION_CHECKED_OUT`, `WORK_ORDER_ACCEPTED`, `WORK_ORDER_CANCELLED`, `SERVICE_VISIT_CANCELLED`, `SERVICE_VISIT_MARKED_MISSED`, `CORRECTIVE_ACTION_PLAN_SUBMITTED`, `CORRECTIVE_ACTION_PLAN_APPROVED`, unknown) | none |

Adding a key or a code to the allowlist is a reviewed code change with a test; the default is to omit.

### 4.2 Actor classification (D5)

`SYSTEM` when `actorId` equals a constant in `SystemActors`; `USER` when the id resolves to an existing user **of the same tenant** (existence check only, one set-based query per page, no data other than existence read); otherwise `UNKNOWN`. Nothing about the user other than the id is returned.

---

## 5. Keyset cursor contract (D6)

**Sort key:** (`occurred_at` DESC, `audit_history_id` DESC). Same-millisecond rows (for example a Work Order cancel and the Visit cancels it cascades) are ordered deterministically by `audit_history_id`; the order inside one timestamp is stable but is not a causal order.

**Predicate (next page):** rows strictly after the cursor position: `occurred_at < @ts OR (occurred_at = @ts AND audit_history_id < @id)`. The comparison is executed **in SQL** (uniqueidentifier ordering differs from .NET `Guid` ordering, so it is never evaluated in memory). No `Guid` comparison and no pagination is ever performed on the client or in memory (see *Ordering, index and read-volume facts*).

**`hasMore`:** read `pageSize + 1` rows; the extra row proves there is more and is not returned. No `COUNT`.

**Cursor value:** opaque, URL-safe Base64 (no padding) of 57 bytes:

| Bytes | Content |
|---|---|
| 0 | format version `0x01` |
| 1–8 | `occurredAt` UTC ticks, `int64` big-endian (exact round trip of the `datetime2(3)` value) |
| 9–24 | `auditHistoryId` (16 bytes) |
| 25–56 | HMAC-SHA256 tag |

**Integrity and binding.** The tag is `HMAC-SHA256(key, context ‖ bytes[0..24])` with

`context` = `"AUD-TL-v1|" + tenantId + "|" + normalizedEntityType + "|" + workOrderId`

where `tenantId` comes from the **authenticated server context** (the validated principal, never the request), `normalizedEntityType` is the route `entityType` upper-cased with the invariant culture, `workOrderId` is the route `entityId`, and `bytes[0..24]` (version, ticks, `auditHistoryId`) is the cursor payload. None of the three context values is stored in the token or read from it. Verifying the tag needs **no database access and no Work Order lookup**. The key is derived with HKDF-SHA256 from the existing `Jwt:SigningKey` with a fixed purpose string (`"rr:audit-timeline-cursor:v1"`), so no new secret or configuration key is introduced and the key is separated from token signing. Rotating the Jwt key invalidates outstanding cursors (a `400`; the client restarts from the first page). The tag is compared in constant time.

**Validation order and disclosure.** The order is fixed (§4 *Check order*):

1. `401` — no or unusable authentication.
2. `403` — authenticated, but the role has no `Audit.TimelineRead`.
3. `400` — `pageSize` below 1, or cursor *syntax*: length ≤ 128 characters before decoding, valid Base64url, decoded length exactly 57 bytes.
4. `400` — cursor HMAC, version or binding: the tag is verified first (tenant from the server context, normalized entity type, route Work Order id, payload); only for an authentic payload are the version (`0x01`), the ticks range and the non-empty `auditHistoryId` checked. One generic message; nothing distinguishes the cause.
5. `404` — the normalized entity type is not `WORK_ORDER`, **or** the scoped lookup through `IDataScope.WorkOrders(user)` (tenant, Site, ownership) finds nothing. Only now is the database touched.

What this order does and does not protect:
- The `400`/`404` split depends only on the caller's own input (is the cursor well-formed and authentic for this tenant, entity type and id) and on the scoped lookup. A `400` is decided **before and regardless of** whether the Work Order exists, so it carries no information about existence. Without a cursor, every non-reachable case is one `404`.
- This sheet does **not** claim that verifying the signature *after* the scope check prevents existence disclosure. It would not: a `400` for one id and a `404` for another, decided after a lookup, is an existence oracle. The v1.0 wording is withdrawn; the order above removes the oracle by not looking anything up before the cursor is accepted.
- Accepted residual: an authentic cursor can only be obtained from an earlier `200` for that tenant and Work Order. A caller who replays a cursor it received from someone else for a Work Order outside its own scope sees `404` (authentic, then the lookup fails) rather than `400`. That reveals only that the shared cursor was authentic, which its source already knew; a prober cannot forge a tag, so arbitrary ids cannot be probed. Binding the cursor to the user id is not adopted (D6 fixes the binding inputs) and can be revisited.

**Scope safety.** The cursor carries a *position* only. After it is accepted, scope and the child entity ids are derived on the server from the scoped Work Order, so a cursor cannot widen the result set. A cursor from another Work Order, tenant, entity type or environment fails step 4 and returns no data.

**Stability note (documented limitation).** Append-only plus a descending keyset means newly written events appear above an already-loaded page and never duplicate or shift older rows. A row committed late with an earlier timestamp (a long transaction that commits after a client has passed that position) can be missed during one paging session; a refresh from the first page resolves it. The Angular de-duplication by `auditId` is a second layer, not the primary guarantee.

**Ordering, index and read-volume facts (these replace the v1.0 read-volume statement).**
- The current index is `IX_audit_history_timeline` on `(tenant_id, entity_type, entity_id, occurred_at DESC)`.
- `audit_history_id` is **not** a declared key column of that index. Whether SQL Server can use it for seeking or ordering (it is the clustering key stored with each index row if the primary key is clustered — to be verified from the model and the database) is **not assumed**; the actual plan must show it.
- `WORK_ORDER` and `REPAIR_REQUEST` have one `entity_id` each, but `SERVICE_VISIT`, `WORK_SESSION` and `CORRECTIVE_ACTION` can have **several** `entity_id` values per Work Order.
- A correct query may therefore need multiple seeks (one per entity id), `IN` lists, `UNION ALL`/`Concat`, a merge and a sort. This sheet does not fix the query shape.
- `occurred_at` is `datetime2(3)`: rows that share a millisecond (for example a Work Order cancel and the Visit cancels it cascades, or a bulk of rows with one timestamp) can make the number of rows read **exceed `pageSize + 1`**, because every row tied with the boundary timestamp may have to be read and ordered by `audit_history_id`. **No fixed upper bound on rows read is claimed.** The v1.0 statement of about 5 × (`pageSize` + 1) rows is withdrawn.
- **Correctness comes before optimization.** The contract is `ORDER BY occurred_at DESC, audit_history_id DESC`, the keyset predicate above, and `hasMore` from the one extra row. No optimization (a per-branch `TOP`, a different shape, an index) may change the returned set or its order.
- **Prohibited:** comparing `Guid` values in memory, paginating or sorting audit rows in memory, and loading a whole timeline in order to slice it. The ordering and the keyset comparison are SQL Server's.
- No index and no migration is added now (D12).

**Phase 1 executable spike (gate before the query is final).** Phase 1 must first demonstrate, with an automated test or script and recorded output:
a. **GUID ordering** — the ordering and the keyset predicate follow SQL Server `uniqueidentifier` ordering. The oracle is the database's own `ORDER BY audit_history_id`, never a .NET sort, and the fixture uses ids whose SQL order differs from the .NET `Guid` order (the premise is asserted so the test cannot pass vacuously).
b. **Server-side keyset** — the captured SQL contains the `(occurred_at, audit_history_id)` comparison, and neither it nor the paging runs on the client (EF translation, or an explicitly parameterised SQL predicate if EF cannot translate it).
c. **Equal timestamps** — no row is missing or duplicated when many rows share one `occurred_at` (§8.1, the 150-row case).
d. **SQL shape and actual execution plan** for a Work Order with many Visit, Work Session and Corrective Action entity ids, for the first page and a deep page.
e. **Logical reads and rows read** for the first page and a deep page, for Work Orders of about 20, 200 and 2,000 events (`STATISTICS IO` or Query Store), recorded as evidence.

**Stop rule.** If the correct query does not meet the performance target (p95 ≤ 3.0 s under the §7 profile), work stops and an index change is proposed to the owner as a **new decision** before any migration is created.

---

## 6. Security / PII rules (binding for implementation and review)

1. Tenant, actor and scope are derived on the server (BR-16); nothing in the URL or query string is authority beyond naming the Work Order, which is resolved through `IDataScope.WorkOrders` only after the cursor (if any) has been accepted.
2. The audit query always filters by the caller's `tenant_id` and by entity ids derived from the scoped Work Order. No client-supplied id (route, query or cursor) reaches the audit table directly, and cursor verification uses only server-derived context and runs before any database access (§5).
3. `403` is a role decision; `404` is identical for missing, other tenant, other Site, non-owner and unsupported entity type — same status, code and body shape. A cursor that is not authentic for the tenant, entity type and route id is `400` before any lookup, so no `400`/`404` difference reveals whether a Work Order exists (§5).
4. `old/new_value_json`, `reason`, `correlation_id` and the actor's email/name are never part of the response (D4/D5/D10). The query never selects `reason`, `old_value_json` or `correlation_id`; `new_value_json` is selected only for action codes with an allowlist entry, only to build `details`, and is never returned as text.
5. The `details` allowlist (§4.1) is the only way payload data leaves the server; unknown keys and codes default to omitted.
6. The response carries no cost amount, currency, contact snapshot/email/phone, Work Summary text or plan text, even for roles that may read them through their own endpoints.
7. Logs and metrics for this endpoint carry the correlation id and safe entity identifiers only — no payload, cursor value or actor details.
8. The Angular timeline stores nothing in `localStorage`/`sessionStorage`; the cursor lives in component state only.
9. The endpoint is read-only: no write path, no audit row on read (D8), `AsNoTracking`.
10. Rate limiting follows the existing endpoint-class policy; nothing new is added here.

---

## 7. Performance profile (D11)

**Target (recorded, not a CI gate this round):** p95 ≤ 3.0 s end to end for the timeline endpoint under the profile below, measured under PERF-DEC-01's recommended statistic. Pass/fail is recorded as evidence; the performance run is not added to the CI workflow.

**Tool:** k6, 100 virtual users. `k6` is not installed on the development machine (F16); how it is run (local binary or container) is decided in the performance phase and does not change the repository's CI. *(Phase 2 ran the official k6 v2.3.0 release binary from a scratch folder; see §13.)*

**Dataset profile** (`docs/11` p.2 shape; seeded by a script outside the migrations, never by an application path):
- 2 tenants × 2 Sites; authenticated users per role from a seed script (credentials are test values and are not committed);
- *Smoke tier:* about 10,000 Work Orders (about 300,000 `audit_history` rows) to validate the scripts and the plan;
- *Baseline tier:* 100,000 Work Orders per Site, with about 20 audit rows per Work Order on average (several million rows), plus a heavy tail of Work Orders with about 200 and about 2,000 events (many Visits/Sessions, several corrective cycles).

**Load:** 5-minute ramp-up, 30-minute steady state, 5-minute ramp-down (the `docs/11` default proposal). Mix: first page of random in-scope Work Orders (about 80 %), follow-up pages through `nextCursor` (about 20 %), weighted across the three event-count classes.

**Measured and recorded:** per endpoint tag p50 / p95 / p99, throughput (requests per second), error rate, count of 5xx and timeouts, plus DB timing and the generated SQL / actual execution plan (or Query Store) of the timeline query for the first page, a deep page, a 2,000-event Work Order and a negative case.

**Cross-scope negative traffic** runs as a separate k6 scenario tagged `traffic=negative` and is reported separately: other-Site Work Order, other-tenant Work Order, unsupported `entityType`, forged/foreign cursor. Its latency and its 403/404/400 counts never mix with the positive-path statistics.

**Index rule:** the existing `IX_audit_history_timeline` is used first. A new or changed index is proposed only after the captured plan shows a problem, and then as its own decision and migration, outside this ticket's V1 contract. The stop rule of §5 applies: a correct query that misses the target stops the work and returns an index change to the owner as a new decision before any migration.

---

## 8. Acceptance criteria

1. `GET /api/v1/entities/WORK_ORDER/{id}/timeline` returns `200` with the items, `nextCursor` and `hasMore` shape of §4 for an in-scope Work Order, for each of REQUESTER (own), APPROVER, COORDINATOR, TEAM_LEAD and SUPERVISOR.
2. TECHNICIAN and ADMINISTRATOR receive `403`; an unauthenticated call receives `401`.
3. Missing, other-tenant, other-Site, non-owner-REQUESTER Work Orders and any `entityType` other than `WORK_ORDER` receive an identical `404`.
4. The timeline contains, for a full-lifecycle Work Order, the convert event, the Work Order events, the Visit, Session and Corrective Action events, and nothing from other Work Orders, other tenants or other entity types, and no other Repair Request event.
5. The order is `occurredAt DESC, auditId DESC`; same-timestamp rows have a stable, repeatable order.
6. Walking every page with several `pageSize` values returns every event exactly once, with `hasMore`/`nextCursor` consistent; `pageSize` below 1 is `400`, above 100 is clamped; a malformed, tampered, truncated, foreign-Work-Order or foreign-tenant cursor is `400` and never returns data.
7. No response contains `old/new` JSON, `reason`, `correlationId`, `totalAmount`, `currencyCode`, `acceptanceContactSnapshot`, an email, a phone number, note text, Work Summary text or plan text; `details` contains only §4.1 keys.
8. Actors are `USER`, `SYSTEM` or `UNKNOWN` with no further identity data.
9. A GET adds no `audit_history` row; non-GET verbs return `405`.
10. No schema change, no migration, no change to existing audit writers.
11. The query is correct first (ordering and keyset predicate evaluated by SQL Server; no client-side `Guid` comparison or pagination). The Phase 1 spike of §5 (a–e) is executed and its generated SQL, actual execution plan, logical reads and rows read are recorded as evidence. No read-volume bound is assumed. No index or migration is created unless a new owner decision follows the §5 stop rule.
12. Performance evidence (p50/p95/p99, throughput, 5xx, negative traffic reported separately) is recorded for the k6 profile of §7; the p95 result is recorded against the 3.0 s target without becoming a CI gate.
13. Angular: the Work Order Detail shows a Timeline section with loading, empty and error (with retry) states, "Load more" that appends and de-duplicates by `auditId`, readable event labels, browser-local times with a UTC `<time datetime>`/`title`, accessible semantics and no browser storage of timeline data; a `403`/`404` shows a message without hiding the rest of the page.
14. All existing and new tests pass (Domain, Application, ApiTests, IntegrationTests, Angular unit tests), the Angular production build passes, and `has-pending-model-changes` reports no changes.
15. `docs/13` is reconciled (new §4.26 for this ticket; §1 status table including the stale RR-API-003, RR-API-010, CA-*, WO-API-011 and Close rows; §3.1 policy table; §6 traceability) and the evidence is referenced from this sheet.
16. The validation order of §4/§5 holds: no authentication with a malformed cursor → `401`; a wrong role with a malformed cursor → `403`; a malformed cursor is `400` whether or not the Work Order exists (identical response for an existing and a non-existing id); a cursor that is syntactically valid but not authentic for the tenant, normalized entity type and route id is `400` and triggers no Work Order lookup; only an authentic cursor (or no cursor) reaches the scoped lookup, where an unsupported entity type, a missing Work Order or an out-of-scope Work Order is `404`.
17. The mandatory same-timestamp cases of §8.1 pass, including the 150-row case, with the order checked against SQL Server `uniqueidentifier` ordering.

### 8.1 Mandatory keyset test cases (same-timestamp and ordering)

| ID | Case | Must assert |
|---|---|---|
| KS-1 | **150 audit rows with the same `occurredAt`** attached to one Work Order (spread over the `WORK_ORDER` id and several `SERVICE_VISIT`, `WORK_SESSION` and `CORRECTIVE_ACTION` ids of that Work Order), distinct `audit_history_id` values, seeded directly through the database. Walk every page with `pageSize` 1, 7, 20, 50, 100, 149, 150 and 151. | The union of all pages is exactly the 150 `auditId` values; **no duplicate and no missing id**; `hasMore`/`nextCursor` are consistent on every page, and the last page has `hasMore = false` and `nextCursor = null`; the returned sequence equals the oracle order obtained from the database itself (`SELECT audit_history_id … ORDER BY audit_history_id DESC` executed by SQL Server, **not** a .NET `Guid` sort); the fixture ids are chosen so that the SQL order differs from the .NET `Guid` order, and that premise is asserted; page boundaries fall inside the tied group. |
| KS-2 | Several timestamps, each with a tied group larger than `pageSize` and groups of size 1, with page boundaries at the start, middle and end of a group. | Same exactly-once and ordering assertions; `occurredAt` is non-increasing across the whole walk. |
| KS-3 | Cursor taken from the last item of the final page. | `200`, empty `items`, `hasMore = false`, `nextCursor = null`. |
| KS-4 | New events appended (including one with the same `occurredAt` as an already-delivered position) between two page requests. | No already-delivered row is duplicated or shifted; the documented late-commit limitation (§5) is the only permitted gap. |
| KS-5 | The validation-order cases of criterion 16. | Exact status codes and identical bodies as specified. |
| KS-6 | Captured SQL of KS-1 and KS-2. | The keyset comparison and the ordering are in the SQL text; there is no client-side comparison or paging. |

---

## 9. Deferred / out of scope

- Any `entityType` other than `WORK_ORDER` (a Repair Request timeline, Visit-only or Session-only timelines) and every Repair Request event other than `REPAIR_REQUEST_CONVERTED` on the Work Order timeline.
- A role-aware disclosure of `reason` and of before/after values; an actor directory (email/display name); `correlationId`; grouping of paired events; filters, search and sort options.
- A Site time-zone model (`time_zone_id`) and Site-local display.
- Security-auditing of timeline reads (D8) and any change to existing audit writers, or a `work_order_id` column/backfill/migration on `audit_history` (D12).
- UC-REP-001 / REP-API-001 (search, reports, export) and the rest of FR-09; SLA and Notification events; Time Correction (held branch, untouched); Technician access to the timeline.
- Making the k6 performance run a mandatory CI gate (D11).

---

## 10. Traceability

`UC-WO-002`; `AUD-API-001`; `FR-09`; `BR-02`; `BR-16`; `TC-PERF-001/002` (timeline read performance); the "View/track scope" security test case (`docs/06` p.4); `docs/08` §5 and §10.2 (audit timeline index and plan evidence); `docs/12` DEC-S2-001-03 (Work Order read scope); `docs/13` §3.1 and §4.25 (policy table; the most recent Work Order audit writer).


---

## 11. Phase 1A spike results — SQL Server executable query spike (recorded 7 October 2026)

**Scope.** Phase 1A only: test/spike code under `tests/RepairRequest.IntegrationTests/Audit/TimelineSpike/` (no API, Application service, policy, Infrastructure store, Angular, schema, index, migration or configuration change). The sheet's §1–§10 are unchanged; this section records evidence. **Result: §11.8 = A, with conditions.**

### 11.1 Environment and method

| Item | Value |
|---|---|
| Server | SQL Server 2019 Express LocalDB `15.0.4382.1` (X64), the existing `RepairRequestDb` on this instance is at database compatibility level 150. No other provider was used. |
| Schema | Created by `MigrateAsync()` from the repository's real migrations into a disposable database (`RepairRequestDb_TimelineSpike`), so the real `audit_history` table and the real `IX_audit_history_timeline` are under test. |
| `audit_history` indexes (read from `sys.indexes`) | `PK_audit_history` — CLUSTERED, primary key, unique: `(audit_history_id)`. `IX_audit_history_timeline` — NONCLUSTERED: `(tenant_id, entity_type, entity_id, occurred_at DESC)`. **No other index.** |
| Data | 504,214 audit rows: 500,000 random "noise" rows over 3 tenants, 6 entity types and 40,000 entity ids (`datetime2(3)` timestamps over 120 days), plus six named Work Order scenarios and their decoys. Statistics were updated with `FULLSCAN`. For context, a bare `COUNT_BIG(*)` costs 8,980 logical reads. |
| Scenarios (audit events / distinct entity ids the query must cover) | WO20 (20 / 7), WO200 (200 / 21), WO2000 (2,000 / 92), MANYIDS (711 / 232), KS1 (150, one timestamp / 10), MIX (162, nine tie groups / 17). Each has a Work Order, several Visit, Work Session and Corrective Action ids and, except KS1/MIX, the originating `REPAIR_REQUEST_CONVERTED` row. |
| Decoys (never to appear) | Same ids and types under another tenant; the same ids under the wrong entity type; the originating request's other events (`…_SUBMITTED`, `…_APPROVED`); an unrelated converted request; another Work Order of the same tenant. |
| Ordering oracle | An independent, un-paged `ORDER BY occurred_at DESC, audit_history_id DESC` executed by SQL Server. .NET `Guid` ordering is used only to assert the premise (that it differs), never to produce an expected value; no result is sorted, compared or paged in memory. |
| Plan and I/O evidence | The exact SQL text and parameters EF Core sent (captured by a `DbCommandInterceptor`) or the handwritten SQL were re-executed under `SET STATISTICS IO ON` / `SET STATISTICS XML ON` (actual plan with runtime row counters). |
| Stand-in | The child entity ids are supplied by the test; resolving them from the scoped Work Order is production work (Phase 1B) and is not measured here. |

**Query shapes compared** (all return the same rows in the same order — each page is compared with the oracle):

| Shape | What it is |
|---|---|
| `EfOr` | One EF Core query: OR over the five source branches, keyset predicate through `Guid.CompareTo`, `TOP(n+1)`. |
| `EfUnion` | EF Core: per-branch `TOP(n+1)` merged with `Concat` (UNION ALL), then `TOP(n+1)`. |
| `SqlOr`, `SqlUnion` | The same two shapes as handwritten, parameterized T-SQL. |
| `SqlTwoStep` | Parameterized T-SQL, one statement: the page is chosen with `TOP(n+1)` over **index-only columns** (`audit_history_id`, `occurred_at`), then only those rows are joined back to the table by primary key for the remaining columns. |
| `SqlTwoStepRange` / `EfTwoStepRange` | `SqlTwoStep` plus a redundant, sargable `occurred_at <= @ts` next to the tuple predicate; the EF Core equivalent is a `TOP(n+1)` subquery joined by key. |

### 11.2 Results per spike item

| # | Item | Result | Evidence (automated tests, all passing) |
|---|---|---|---|
| 1 | SQL Server `uniqueidentifier` ordering | **Proven.** SQL Server compares the trailing six bytes first; for the crafted pair `ffffffff-0000-…-000000000001` and `00000000-0000-…-ffffffffffff`, SQL Server says the first is smaller while .NET `CompareTo` says it is greater. On the real 150-row tie group the SQL Server order differs from the .NET order. Every walk equals the database's own `occurred_at DESC, audit_history_id DESC`. | `SqlServerGuidOrdering_DiffersFromDotNetGuidOrdering`; premise asserted inside every KS-1 case. |
| 2 | Server-side keyset predicate | **Proven, with EF Core.** EF Core 10 translates `Guid.CompareTo` into a native `uniqueidentifier` comparison, so no parameterized-SQL fallback is required. The captured SQL (deep page) contains `[a].[occurred_at] <= @ts AND ([a].[occurred_at] < @ts OR ([a].[occurred_at] = @ts AND [a].[audit_history_id] < @id))` and `ORDER BY [a].[occurred_at] DESC, [a].[audit_history_id] DESC`. | `KeysetPredicate_IsExecutedInSqlServer_ForEveryVariant` (regex over the captured SQL, plus page equality with the oracle) for all 7 shapes. |
| 3 | Composite source set | **Proven.** For WO20, WO200, WO2000 and MANYIDS the walk returns exactly the seeded events (set equality and oracle order), includes all five source kinds, every Visit id is represented, the only Repair Request row is `REPAIR_REQUEST_CONVERTED`, and no decoy (other tenant, wrong type, other Work Order, other request events) appears. | `CompositeSourceSet_ReturnsExactlyTheSeededEvents_AndNoDecoys` ×28. |
| 4 | KS-1 equal timestamp (150 rows) | **Proven** — see §11.4. | `KS1_150RowsWithOneTimestamp_…` ×56. |
| 5 | Mixed timestamps and cursor boundaries | **Proven** — see §11.5. | `MixedTimestamps_…` ×98 (+ coverage, cursor-position and append tests). |
| 6 | Query shape and index behaviour | **Measured** — see §11.3. | `CollectPlanEvidence_ForEveryScenarioVariantAndPage` (91 measurements, every page checked against the oracle). |
| 7 | Index decision gate | **A — pass without a new index**, with conditions — §11.8. | — |

Spike totals: 207 tests (`Spike_RunsOnRealSqlServerLocalDb` 1, GUID ordering 1, keyset predicate 7, composite 28, KS-1 56, mixed 98, boundary coverage 1, cursor positions 7, append / late commit 7, evidence 1). A mutation check (changing the keyset predicate to `audit_history_id <= @id`) made the SQL-shape KS-1 cases fail, so the tests do detect a wrong predicate. The complete `RepairRequest.IntegrationTests` project: 460 passed (253 existing + 207 spike).

### 11.3 Query shape, index behaviour and read volume

Measured, first page and a deep page (cursor at about 80 % of the timeline), page size 20 (`TOP(21)`), logical reads on `audit_history` only, median of 15 warm runs on one client (informational timing, **not** a latency or p95 claim):

| Scenario | Events | Entity ids | Shape | Page | Rows returned | IX seeks (executions) | IX rows read | PK lookups (executions) | Logical reads | Median ms |
|---|---|---|---|---|---|---|---|---|---|---|
| WO20 | 20 | 7 | EfOr | first | 20 | 7 | 22 | 23 | 97 | 0.8 |
| WO20 | 20 | 7 | EfUnion | first | 20 | 7 | 22 | 22 | 94 | 1.4 |
| WO20 | 20 | 7 | SqlTwoStep | first | 20 | 7 | 22 | 23 | 97 | 0.9 |
| WO20 | 20 | 7 | EfTwoStepRange | first | 20 | 7 | 22 | 23 | 97 | 0.6 |
| WO20 | 20 | 7 | EfOr | deep | 3 | 7 | 22 | 6 | 46 | 0.5 |
| WO20 | 20 | 7 | EfUnion | deep | 3 | 7 | 22 | 5 | 43 | 1.0 |
| WO20 | 20 | 7 | SqlTwoStep | deep | 3 | 7 | 22 | 6 | 46 | 0.6 |
| WO20 | 20 | 7 | EfTwoStepRange | deep | 3 | 7 | 15 | 6 | 46 | 0.5 |
| WO200 | 200 | 21 | EfOr | first | 21 | 21 | 202 | 203 | 716 | 1.9 |
| WO200 | 200 | 21 | EfUnion | first | 21 | 25 | 234 | 202 | 708 | 3.2 |
| WO200 | 200 | 21 | SqlTwoStep | first | 21 | 21 | 202 | 24 | 158 | 1.0 |
| WO200 | 200 | 21 | EfTwoStepRange | first | 21 | 21 | 202 | 24 | 158 | 1.3 |
| WO200 | 200 | 21 | EfOr | deep | 21 | 21 | 202 | 42 | 223 | 1.0 |
| WO200 | 200 | 21 | EfUnion | deep | 21 | 21 | 202 | 41 | 209 | 2.2 |
| WO200 | 200 | 21 | SqlTwoStep | deep | 21 | 21 | 202 | 24 | 158 | 1.5 |
| WO200 | 200 | 21 | EfTwoStepRange | deep | 21 | 21 | 175 | 24 | 158 | 1.2 |
| WO2000 | 2000 | 92 | EfOr | first | 21 | 92 | 2002 | 2003 | 6637 | 14.6 |
| WO2000 | 2000 | 92 | EfUnion | first | 21 | 92 | 2002 | 2002 | 6546 | 15.3 |
| WO2000 | 2000 | 92 | SqlTwoStep | first | 21 | 92 | 2002 | 24 | 566 | 4.1 |
| WO2000 | 2000 | 92 | EfTwoStepRange | first | 21 | 92 | 2002 | 24 | 566 | 3.7 |
| WO2000 | 2000 | 92 | EfOr | deep | 21 | 92 | 2002 | 402 | 1733 | 4.2 |
| WO2000 | 2000 | 92 | EfUnion | deep | 21 | 92 | 2002 | 401 | 1701 | 6.9 |
| WO2000 | 2000 | 92 | SqlTwoStep | deep | 21 | 92 | 2002 | 24 | 566 | 2.4 |
| WO2000 | 2000 | 92 | EfTwoStepRange | deep | 21 | 92 | 1748 | 24 | 562 | 2.2 |
| MANYIDS | 711 | 232 | EfOr | first | 21 | 232 | 713 | 714 | 3373 | 8.4 |
| MANYIDS | 711 | 232 | EfUnion | first | 21 | 232 | 713 | 713 | 3255 | 8.6 |
| MANYIDS | 711 | 232 | SqlTwoStep | first | 21 | 232 | 713 | 24 | 1250 | 3.5 |
| MANYIDS | 711 | 232 | EfTwoStepRange | first | 21 | 232 | 713 | 24 | 1250 | 4.4 |
| MANYIDS | 711 | 232 | EfOr | deep | 21 | 232 | 713 | 145 | 1630 | 5.1 |
| MANYIDS | 711 | 232 | EfUnion | deep | 21 | 232 | 713 | 144 | 1546 | 4.2 |
| MANYIDS | 711 | 232 | SqlTwoStep | deep | 21 | 232 | 713 | 24 | 1250 | 3.1 |
| MANYIDS | 711 | 232 | EfTwoStepRange | deep | 21 | 232 | 698 | 24 | 1250 | 3.2 |

What the actual plans show:
- **Seeks only, no scans.** In all 91 measurements the only access to `audit_history` is `Index Seek` on `IX_audit_history_timeline` plus `Clustered Index Seek` on `PK_audit_history` for lookups. There is no `Index Scan`, `Clustered Index Scan` or table scan, and no sort spill. The index is used with an equality prefix on `(tenant_id, entity_type, entity_id)`; for the OR and two-step shapes the scan count equals the number of distinct entity ids the query covers (7, 21, 92, 232, 10, 17 — one seek execution per id); `EfUnion` adds a few extra seeks.
- **Read volume is not bounded by the page size.** For every shape the index seeks read **all** index rows of the Work Order's timeline on the first page (202 of 202 for WO200, 2,002 of 2,002 for WO2000, 713 of 713 for MANYIDS), and a deep page still reads about the same number (for example WO2000 on the deep page: 2,002 index rows read, 401 passing the predicate, 21 returned; 1,751 read with the added range). The keyset predicate is applied as a residual predicate on the index rows; the added `occurred_at <= @ts` trims a small part of that read on deep pages only. Rows read therefore grows with the number of events and with the number of entity ids of the Work Order, not with `pageSize`. No bound is claimed beyond what is measured here.
- **Plain shapes do a key lookup for every row.** `EfOr` / `EfUnion` / `SqlOr` / `SqlUnion` look up the clustered index for every candidate row **before** the top-N sort (203 lookups for WO200, 2,003 for WO2000), because the projected columns (`action_code`, `from_state`, `to_state`, `actor_id`, the conditional `new_value_json`) are not in the index. That is the dominant cost: 6,640 logical reads for one first page of the 2,000-event Work Order, which is 74 % of a full index scan of the table.
- **The two-step shape removes the per-row lookups.** `SqlTwoStep` and `EfTwoStepRange` choose the page from index-only columns and look up only the page's 21 rows: 2,000 events 6,640 → 577 logical reads, 200 events 717 → 159, 711 events / 232 ids 3,363 → 1,248, 20 events 97 → 97. The remaining cost is the index read of the Work Order's rows plus one seek per entity id. EF Core expresses it (`EfTwoStepRange`) with the same plan as the handwritten SQL.
- **Sorts.** 4–8 `Sort` operators appear: sorting the `IN`-list seek keys and the final top-N over the Work Order's index rows. All are in memory (no spill) at the sizes tested.
- **Evidence artifacts.** The full evidence (all 91 measurements, plan operator summaries, captured SQL) is regenerated by `TimelineSpikeEvidenceTests` into the file named by `TIMELINE_SPIKE_EVIDENCE_PATH` (default `timeline-spike-evidence.md` beside the test binaries; the actual plan of WO2000 / `EfOr` / deep page is saved as a `.sqlplan`). The row count of the noise is set by `TIMELINE_SPIKE_NOISE_ROWS` (default 500,000).

### 11.4 KS-1 — 150 audit rows with one `occurredAt` (all 7 shapes × all 8 page sizes = 56 cases, all pass)

150 rows spread over the Work Order id, 3 Visit ids, 3 Work Session ids and 2 Corrective Action ids, one identical `occurred_at`, ids random (SQL Server order ≠ .NET order, asserted). Every walk returned all 150 ids, no duplicate, no missing id, in the oracle order (SQL Server `uniqueidentifier` DESC); every page boundary except the end falls inside the tie group; the final page has `hasMore = false` and no next cursor.

| `pageSize` | Pages | Size of the last page | Result |
|---|---|---|---|
| 1 | 150 | 1 | pass |
| 7 | 22 | 3 | pass |
| 20 | 8 | 10 | pass |
| 50 | 3 | 50 | pass |
| 100 | 2 | 50 | pass |
| 149 | 2 | 1 | pass |
| 150 | 1 | 150 (exact boundary) | pass |
| 151 | 1 | 150 | pass |

### 11.5 Mixed timestamps, cursor boundaries and appended events

- **MIX (162 rows, tie groups of 1, 25, 1, 60, 3, 40, 1, 30, 1)**, page sizes 1, 2, 3, 7, 20, 25, 26, 27, 30, 54, 81, 161, 162, 163 on all 7 shapes: every walk equals the oracle, no duplicate, `occurred_at` never increases, the last page is exactly full when `162 % pageSize = 0`, and the final page has no next cursor. The sizes were chosen and asserted to include boundaries inside a tie group, at the end of a tie group and at exact multiples.
- **Cursor positions:** a cursor at the start, the middle and the end of the 60-row tie group, one row before its start, and the second-last and last row of the timeline all continue exactly after the cursor row (compared with the oracle slice), on all shapes. A cursor on the last row returns an empty page.
- **Appended events (`EventsAppendedBetweenPages_…`, 7 shapes).** After reading two pages of 7, five rows were inserted: one newer than everything, one at an already-delivered timestamp, one at the cursor's timestamp with an id that sorts before the cursor, one at the cursor's timestamp with an id that sorts after it, and one older than everything. (Which side of the cursor an id falls on was decided by SQL Server, never in memory.) Continuing the same walk: nothing already delivered reappears or shifts, nothing is duplicated, the rows that sort after the cursor are delivered exactly once in the oracle order, and the rows that sort before the cursor are not delivered in that session. A fresh walk from the first page then returns every row in the oracle order.
- **Late-commit limitation, stated precisely.** The "late commit" in §5 means a row whose `occurred_at` is already behind the clock when it becomes visible, because its transaction committed late. Relative to a client that is part-way through a walk: a late row that sorts **after** the cursor (older `occurred_at`, or the same timestamp with a smaller id) **is** delivered, once, in order; a late row that sorts **before** the cursor (a newer `occurred_at`, or the same timestamp with a larger id) **is not** delivered in that paging session and appears on the next fresh walk. That is the limitation the sheet accepts; the spike confirms its exact shape and that nothing is duplicated or shifted.

### 11.6 EF Core versus parameterized SQL

EF Core is sufficient: `Guid.CompareTo` is translated to a server-side `uniqueidentifier` comparison with correct semantics, so **parameterized SQL is not needed and no comparison, sort or paging happens on the client**. For reference, the parameterized SQL used for comparison binds `@ts` as `datetime2(3)` (the column type, exact tick round trip), `@id` as `uniqueidentifier`, `@take` as `int`, `@tenant` / `@wo` / `@rr` as `uniqueidentifier` and one `uniqueidentifier` parameter per entity id; EF Core binds the same types, with `@p` as the `int` `TOP` value.

### 11.7 Findings and caveats carried into Phase 1B

1. **Do not use the plain shapes** (`EfOr`, `EfUnion`): they look up every row before taking the page. The shape to carry forward is the **two-step** one (`EfTwoStepRange`): choose the page from index-only columns with `TOP(pageSize + 1)`, then join only those rows back by primary key.
2. **Read volume grows with the Work Order's events and entity ids**, not with `pageSize`; a deep page costs about the same as the first. At the sizes measured (up to 2,000 events or 232 entity ids) the two-step shape costs at most about 1,250 logical reads per page. This is evidence, not a bound.
3. **One `IN` parameter per entity id.** EF Core 10 emits `IN (@visits1, @visits2, …)` with one SQL parameter per id (94 parameters at 92 ids, 234 at 232). SQL Server allows at most 2,100 parameters per command, so a Work Order with more than about 2,000 child entity ids needs chunking or a table-valued/JSON parameter. Not measured beyond 232 ids.
4. **Resolving the child ids** (Visits by `work_order_id`, Work Sessions by Visit, Corrective Actions by `work_order_id`) is Phase 1B work and is not measured here; the indexes for it already exist (§2 F5).
5. The `REPAIR_REQUEST` branch filters on `action_code`, which is not in the index; it touches at most the originating request's few rows.
6. The timings above are single-client, warm-cache, local-database figures, recorded for orientation only. The p95 ≤ 3.0 s target (D11) is judged in the k6 phase, not here.

### 11.8 Index decision gate — **A. PASS WITHOUT NEW INDEX** (conditions below)

- **Correctness is proven on SQL Server:** ordering by SQL Server `uniqueidentifier` order, a server-side keyset predicate, exactly-once and no-gap delivery with ties (150 rows on one timestamp, tie groups larger than the page, every boundary position), composite source set with no decoys, and appended-event behaviour.
- **The plan has no clear risk for the chosen shape:** index seeks on the existing `IX_audit_history_timeline` only, no scans, no spills, lookups limited to the page, at most about 1,250 logical reads and about 4 ms warm per page at the largest sizes measured (2,000 events; 232 entity ids).
- **No index and no migration is proposed.** The plain shapes' cost is a query-shape defect, not an index defect, and the two-step shape removes it without a schema change.

Conditions: (1) Phase 1B implements the two-step shape and may not fall back to a plain shape; (2) the read-volume caveat of §11.7 item 2 stays on record and no bound is claimed; (3) the `IN`-list limit of §11.7 item 3 is handled before a Work Order can exceed it; (4) the §5 **stop rule is unchanged** — if the k6 profile of §7 misses p95 ≤ 3.0 s, work stops and an index change returns to the owner as a new decision before any migration is created.


---

## 12. Phase 1B implementation evidence (recorded 7 October 2026)

**Scope.** Production implementation of UC-WO-002 / AUD-API-001 under D1–D12 and the Phase 1A conditions of §11.8. No schema, index, migration or configuration change; no baseline PDF touched; nothing committed. This section records what was actually built and measured.

### 12.1 What was built

| Layer | Items |
|---|---|
| Application (`backend/src/RepairRequest.Application/Audit/`) | contracts (`AuditTimelineSources`, `TimelinePosition`, row/item/actor/page DTOs, outcome), `IAuditTimelineStore`, `AuditTimelineService` (validation order of §5), `AuditTimelineDetails` (the §4.1 allowlist), `ITimelineCursorProtector` + `TimelineCursorFormat` + `TimelineCursorSyntax`; policy `Audit.TimelineRead` (same roles as `WorkOrder.Read`); DI registration |
| Infrastructure (`.../Infrastructure/Audit/`) | `AuditTimelineStore` (scoped source resolution, the two-step OPENJSON page query, same-tenant actor-existence lookup), `TimelineCursorProtector` (HMAC-SHA256, HKDF from `Jwt:SigningKey`, constant-time compare); DI registration |
| API | `AuditTimelineController` (`GET /api/v1/entities/{entityType}/{entityId:guid}/timeline`), `KeysetPageRequests`, response contracts; `Cache-Control: no-store` |
| Angular | `work-order-timeline.models.ts`, `work-order-timeline.labels.ts`, `WorkOrderService.getTimeline`, `WorkOrderTimelineComponent`, integration in `WorkOrderDetailComponent` |
| Tests | Application unit tests; production-store integration tests (`AuditTimelineStoreTests`, `AuditTimelineSourceResolutionTests`, `TimelineCursorProtectorTests`) and an **opt-in** evidence test; API endpoint tests; Angular service, component and detail-integration specs |
| Docs / tooling | `docs/13` §1, §3.1, §4.26, §6 reconciled; `docs/12` §12 pointer; `tests/performance/` k6 script and README (scaffold in Phase 1B; run in Phase 2, §13) |

### 12.2 Production query shape and the OPENJSON strategy

The page is the Phase 1A two-step shape, now with the three child-id sets passed as **one JSON parameter each** and parsed inside SQL Server (`EF.Parameter(list)` → `OPENJSON(@visitIds) WITH ([value] uniqueidentifier '$')`); entity types are server constants; the Repair Request convert branch is a separate `UNION ALL` branch (see §12.3). SQL captured from the production store (allowlist abbreviated):

```sql
SELECT [a1].[audit_history_id], [a1].[occurred_at], [a1].[entity_type], [a1].[entity_id], [a1].[action_code], [a1].[from_state], [a1].[to_state], [a1].[actor_id],
       CASE WHEN [a1].[action_code] IN (<16 allowlisted action codes>) THEN [a1].[new_value_json] END
FROM (
    SELECT TOP(@p) [u].[Id], [u].[OccurredAt]
    FROM (
        SELECT [a].[audit_history_id] AS [Id], [a].[occurred_at] AS [OccurredAt]
        FROM [audit_history] AS [a]
        WHERE [a].[tenant_id] = @tenantId AND (([a].[entity_type] = 'WORK_ORDER' AND [a].[entity_id] = @workOrderId)
           OR ([a].[entity_type] = 'SERVICE_VISIT'      AND [a].[entity_id] IN (SELECT [v].[value] FROM OPENJSON(@visitIds)            WITH ([value] uniqueidentifier '$') AS [v]))
           OR ([a].[entity_type] = 'WORK_SESSION'       AND [a].[entity_id] IN (SELECT [s].[value] FROM OPENJSON(@sessionIds)          WITH ([value] uniqueidentifier '$') AS [s]))
           OR ([a].[entity_type] = 'CORRECTIVE_ACTION'  AND [a].[entity_id] IN (SELECT [c].[value] FROM OPENJSON(@correctiveActionIds) WITH ([value] uniqueidentifier '$') AS [c])))
        -- keyset (added on later pages), both branches:
        --   AND [a].[occurred_at] <= @ts AND ([a].[occurred_at] < @ts OR ([a].[occurred_at] = @ts AND [a].[audit_history_id] < @id))
        UNION ALL
        SELECT [a0].[audit_history_id], [a0].[occurred_at]
        FROM [audit_history] AS [a0]
        WHERE [a0].[tenant_id] = @tenantId AND [a0].[entity_type] = 'REPAIR_REQUEST' AND [a0].[entity_id] = @repairRequestId AND [a0].[action_code] = 'REPAIR_REQUEST_CONVERTED'
    ) AS [u]
    ORDER BY [u].[OccurredAt] DESC, [u].[Id] DESC
) AS [u0]
INNER JOIN [audit_history] AS [a1] ON [u0].[Id] = [a1].[audit_history_id]
ORDER BY [u0].[OccurredAt] DESC, [u0].[Id] DESC
```

- **Compatibility level.** `OPENJSON` needs level 130 or higher. The level detected on the LocalDB instance used for every run is **150** (SQL Server 2019 Express LocalDB 15.0.4382.1); a test asserts `sys.databases.compatibility_level >= 130` and proves `OPENJSON … WITH (Id uniqueidentifier '$')` executes. Azure SQL is created at a higher level; any deployment below 130 would fail the query and must be corrected, not worked around.
- **Parameters.** The first page sends **7** parameters (`@p`, `@tenantId`, `@workOrderId`, `@repairRequestId`, three JSON strings) and a later page **9** (`@ts`, `@id` added) — the same count for 7, 232 and 2,402 child ids; a regression test asserts three `OPENJSON` occurrences, no per-id parameter and an identical parameter count across those sizes.
- **No in-memory work.** Ordering, the `(occurred_at, audit_history_id)` comparison and `TOP` are executed by SQL Server; the store never sorts, compares `Guid`s or pages in memory. The only client-side step is the cursor's constant-time tag comparison.

### 12.3 A regression found by the gate, and its fix

The first production form kept the Repair Request branch (`action_code = 'REPAIR_REQUEST_CONVERTED'`) inside the main `OR`. With `OPENJSON` inside an `OR`, SQL Server expands the `OR` into per-branch seeks and adds exclusion predicates that reference every branch, including `action_code`, which is **not** in `IX_audit_history_timeline`; the actual plan then did a clustered-index lookup for **every candidate row** (310 / 800 / 480 / 410 on the 2,000-event Work Order — 6,410 logical reads for one first page, the same cost as the plain shape the Phase 1A condition forbids). The gate failed on exactly that assertion. The fix is the `UNION ALL` split above (the main `OR` references index columns only; the convert branch is looked up separately and touches only the originating request's few rows). The 2,000-event first page then cost **396–488 logical reads** with 24 primary-key executions (22 rows). This was a query-shape correction within the approved constraints (still two-step, still `OPENJSON`, no index, no migration).

### 12.4 Production-store plan evidence (opt-in run, 500,000 noise rows, page size 20 → `TOP(21)`)

`RUN_TIMELINE_EVIDENCE=1`; `audit_history` about 504,000 rows (500,000 noise + the scenarios); a bare `COUNT_BIG(*)` costs 8,984 logical reads; indexes unchanged (`PK_audit_history` clustered on `audit_history_id`; `IX_audit_history_timeline`). Every page was asserted equal to SQL Server's own ordering; no plan contained a scan of `audit_history`; no sort spill occurred in this run. Median ms is the warm, single-client figure — **orientation only, not a latency or p95 claim**.

| Scenario | Events | Entity ids | Page | Rows returned | Logical reads | IX seeks | IX rows read | PK lookups | Parameters | Median ms |
|---|---|---|---|---|---|---|---|---|---|---|
| WO20 | 20 | 7 | first | 20 | 105 | 7 | 22 | 23 | 7 | 0.9 |
| WO20 | 20 | 7 | deep | 3 | 49 | 7 | 10 | 6 | 9 | 0.3 |
| WO200 | 200 | 21 | first | 21 | 167 | 21 | 202 | 24 | 7 | 1.1 |
| WO200 | 200 | 21 | deep | 21 | 167 | 21 | 42 | 24 | 9 | 0.6 |
| WO2000 | 2,000 | 92 | first | 21 | 488 | 92 | 2,002 | 24 | 7 | 4.0 |
| WO2000 | 2,000 | 92 | deep | 21 | 477 | 92 | 403 | 24 | 9 | 1.5 |
| MANYIDS | 711 | 232 | first | 21 | 1,026 | 232 | 713 | 24 | 7 | 3.1 |
| MANYIDS | 711 | 232 | deep | 21 | 1,025 | 232 | 145 | 24 | 9 | 2.1 |
| BIGIDS | 2,421 | 2,402 | first | 21 | 9,750 | 2,402 | 2,423 | 24 | 7 | 15.7 |
| BIGIDS | 2,421 | 2,402 | deep | 21 | 9,750 | 2,402 | 491 | 24 | 9 | 13.0 |
| KS1 (one timestamp) | 150 | 10 | first / deep / mid-tie | 21 | 125 | 10 | 152 | 23 | 7 / 9 / 9 | 0.5–1.1 |
| MIX (tie groups) | 162 | 17 | first / deep | 21 | 149 | 17 | 164 / 74 | 23 | 7 / 9 | 0.7–1.2 |

### 12.5 Child-id regression (more than 2,100 ids)

- `BIGIDS` (1,300 Visits + 1,000 Work Sessions + 100 Corrective Actions = **2,400 child ids**, 2,402 entity ids with the Work Order and request): the walk returns every seeded event exactly once in SQL Server order, no decoy, with the constant parameter count above; the plan has no `audit_history` scan and its payload lookups are limited to the page.
- Real rows (`AuditTimelineSourceResolutionTests`): **1,150 real Service Visits, each with a real Work Session (2,300 real child ids)** resolve through the production `ResolveWorkOrderSourcesAsync` and are read through one bounded command (23 pages of 100, 2,300 events, exactly once). The API test repeats this through the endpoint with 1,150 Visits + 1,150 Sessions.

### 12.6 Observations and caveats (recorded, not hidden)

1. **Read volume is not bounded by `pageSize`.** It grows with the number of events and entity ids of the Work Order, and a deep page costs about the same as the first. No bound is claimed.
2. **At about 2,400 child ids the one-seek-per-id cost is comparable to a scan.** `BIGIDS` costs 9,750 logical reads (about 1.09 × the 8,984 of a bare `COUNT_BIG(*)` over the 504k-row table) and 13–16 ms warm; the optimizer still chooses seeks because it estimates 50 rows for an `OPENJSON` source. Such a Work Order is far above the sizes the baseline implies; the figure is recorded for the owner.
3. **A small sort spill appears in the default-size run for `BIGIDS`.** With the 100,000-row default fixture the candidate sort for the 2,400-id first page wrote 9 pages (72 KB) to tempdb (19 ms warm, 7,333 logical reads) because of the same 50-row estimate; the 500,000-row evidence run had none. The integration test asserts no spill up to 232 ids and **bounds** the 2,400-id case (at most 100 pages) instead of asserting none. This is not a scan or lookup regression; it is reported for the owner to veto.
4. **Unchanged:** the late-commit limitation (§5, §11.5) and the cursor residual-risk note (§5).

### 12.7 Tests and verification (working tree, default settings)

| Check | Result |
|---|---|
| `dotnet build RepairRequest.slnx` | 0 warnings, 0 errors |
| Domain tests | 489 passed |
| Application tests | 507 passed (421 before; +84 timeline unit tests, +2 policy-catalog tests) |
| ApiTests | 629 passed (601 before; +28 timeline endpoint tests) |
| IntegrationTests | 325 passed, 1 skipped by design (the opt-in evidence test); 253 before; +73 timeline tests (store 40, source resolution 9, cursor protector 24) |
| Angular tests | 215 passed (187 before; +4 service, +20 component, +4 detail-integration specs) |
| Angular production build | passes, no warnings |
| `dotnet ef migrations has-pending-model-changes` | "No changes have been made to the model since the last migration." |

The Phase 1A spike files were removed; their coverage (SQL Server GUID ordering, KS-1, tie groups, cursor positions, appended events / late commit, composite source set, many child ids, server-side keyset, two-step shape, plan assertions) now runs against the production store in `AuditTimelineStoreTests`. The heavy 500,000-row evidence run is opt-in (`RUN_TIMELINE_EVIDENCE=1`, skipped by default); default `dotnet test` and CI seed 100,000 rows.

---

## 13. Phase 2 — performance acceptance and pre-commit audit (recorded 10 October 2026)

**Scope.** A real k6 acceptance run of the Work Order timeline (AUD-API-001), a look at BIGIDS / tempdb behaviour under concurrency, and a read-only audit of the whole working tree against §1–§12 and D1–D12. No schema, index, migration, API contract, authorization or business-rule change; no baseline PDF touched; nothing committed, pushed or opened as a PR.

> **These are local evidence figures from one developer laptop on which k6, the API host and SQL Server all ran together. They are not a production capacity guarantee, and the dataset is far smaller than the `docs/11` baseline tier (§13.3).**

### 13.1 Method and load model
- **Owner decision for this phase:** the acceptance gate is **100 virtual users with 0.5 s think time**; a **zero-think-time saturation run for LARGE and BIGIDS** is supplementary stress evidence and is **not** the gate.
- Test Plan v1.0 (`tests/performance/README.md`) adjusts the `docs/11` default 5 / 30 / 5-minute profile, as `docs/11` allows for a versioned plan: 30 s ramp to 100 VUs, **2 min 30 s hold**, 10 s ramp-down, one run per profile, a discarded 60 s warm-up at 20 VUs.
- Each request is either a **first page** or a **deep page** (50 / 50; `pageSize=20`; cursors precomputed in `setup()` by walking real pages, at about 50 % and 80 % depth) for the callers Supervisor, Coordinator, Team Lead, Approver and the owning Requester (round-robin). **Every response is validated** (status 200, JSON content type, `Cache-Control: no-store`, `items` / `hasMore` / `nextCursor` consistency, item fields and enums, UTC `occurredAt`, non-increasing order, no forbidden property such as `reason` / `correlationId` / raw JSON / e-mail). Any deviation counts into `unexpected_response`, which must be **0**.
- Around every run: a read-only snapshot before and after (`tests/performance/sql/capture-snapshot.sql`), CPU / memory sampling every 5 s, and an API-log scan.

### 13.2 Environment

| Item | Value |
|---|---|
| Machine | ASUS TUF Gaming FX505DT, AMD Ryzen 7 3750H (4 cores / 8 threads), 21.9 GiB RAM (Windows-reported), Windows 11 Home Single Language 10.0.26200 |
| Roles on the machine | k6, the API host and SQL Server all on this laptop (a limitation; CPU shown in §13.6) |
| SQL Server | Microsoft SQL Server 2019 Express (RTM-CU27-GDR) 15.0.4382.1, 64-bit; LocalDB automatic instance `MSSQLLocalDB`; database `RepairRequestDb_PerfTimeline`, **compatibility level 150**, created from the real EF migrations (latest `20260929125930_AddCorrectiveActionRowVersion`; no new migration) |
| .NET | SDK 10.0.401; EF Core 10.0.12; API published with `dotnet publish -c Release`, run as `ASPNETCORE_ENVIRONMENT=Production` on `http://127.0.0.1:5199`, connection string and a random Base64 JWT key from environment variables, no rate limiting (none exists in the API) |
| k6 | **v2.3.0** (commit e088784614, go1.26.8, windows/amd64): the official release archive `k6-v2.3.0-windows-amd64.zip`, SHA-256 `112276d495e5741c968e2bc09ea6196099c1275bd6db9ee0875d173c7148ce43` matched the published `k6-v2.3.0-checksums.txt`; extracted to a scratch folder (no PATH or system change, no binary in Git) |
| Code under test | branch `feature/uc-wo-002-work-order-timeline-audit` on `39612793df1b7f12a9f8d9d0efb65cb213d4e873`, **uncommitted working tree** (Phase 1B plus this phase's tooling) |
| API startup | the API's own lookup seeding inserted 20 Category / Priority rows when it first started on the empty database; nothing else wrote data |

### 13.3 Dataset (seeded by `tests/performance/seed/RepairRequest.PerfSeed`, outside the migrations)

| Profile | Work Orders | Events per Work Order | Child entity ids per Work Order | Real rows |
|---|---|---|---|---|
| SMALL | 100 | 20 | 5 | Visits, Sessions, Corrective Actions are real rows |
| MEDIUM | 50 | 200 | 19 | real |
| LARGE | 20 | 2,000 | 90 | real |
| MANYIDS | 10 | 711 | 230 (232 entity ids) | real |
| BIGIDS | 5 | 2,421 | **2,400** (1,300 Visits, 1,000 Sessions, 100 Corrective Actions; 2,402 entity ids) | real |
| Background | 5,000 Work Orders + Repair Requests across 3 Sites, plus 500,000 noise `audit_history` rows across 3 tenants | | | |

Final row counts: `audit_history` **571,225** (plus the login rows each run appends), `repair_request` 5,187, `work_order` 5,187, `service_visit` 8,900, `work_session` 7,400, `corrective_action` 1,250, 10 users. For every profile the first Work Order's event count equals SQL Server's own oracle (`counts`), and sample timelines walked through the API equal the oracle exactly once and in order (`verify`, §13.8). The ids are regenerated by every `reset` + `seed`; the sizes are deterministic. **The `docs/11` baseline tier (100,000 Work Orders per Site, several million audit rows) was not reproduced**; this is a recorded limitation. SMALL timelines have exactly 20 events, so they have a single page and no deep page.

### 13.4 Acceptance results (think time 0.5 s, 100 VUs, 2 min 30 s hold; milliseconds)

Positive traffic only; setup and login requests are tagged separately and excluded.

| Profile | Requests | p50 | p90 | p95 | p99 | max | Req/s | HTTP failed | Unexpected | p95 ≤ 3.0 s |
|---|---|---|---|---|---|---|---|---|---|---|
| SMALL | 33,236 | 7.2 | 11.2 | 13.6 | 22.1 | 2,121.8 | 174.9 | 0.0 % | 0 | yes |
| MEDIUM | 33,372 | 7.2 | 10.6 | 12.6 | 20.5 | 1,300.5 | 175.6 | 0.0 % | 0 | yes |
| LARGE (run 1) | 33,295 | 8.9 | 13.0 | 15.0 | 22.0 | 39.5 | 175.2 | 0.0 % | 0 | yes |
| LARGE (re-run) | 33,106 | 9.4 | 15.4 | 19.9 | 73.8 | 601.3 | 174.2 | 0.0 % | 0 | yes |
| MANYIDS (supplementary) | 33,266 | 9.3 | 13.3 | 15.4 | 22.1 | 86.2 | 175.1 | 0.0 % | 0 | yes |
| BIGIDS (run 1) | 27,130 | 110.1 | 205.4 | 232.8 | 310.4 | 2,596.3 | 142.8 | 0.0 % | 0 | yes |
| BIGIDS (re-run) | 27,008 | 116.8 | 213.6 | 238.4 | 294.7 | 1,938.8 | 142.1 | 0.0 % | 0 | yes |

| Run | First page: n | p50 | p95 | p99 | max | Deep page: n | p50 | p95 | p99 | max |
|---|---|---|---|---|---|---|---|---|---|---|
| SMALL | 33,236 | 7.2 | 13.6 | 22.1 | 2,121.8 | 0 | — | — | — | — |
| MEDIUM | 16,820 | 7.5 | 13.0 | 21.4 | 1,291.5 | 16,552 | 6.9 | 12.0 | 19.8 | 1,300.5 |
| LARGE (run 1) | 16,640 | 10.0 | 16.2 | 23.7 | 39.5 | 16,655 | 7.7 | 13.0 | 20.0 | 33.3 |
| LARGE (re-run) | 16,414 | 10.6 | 21.3 | 76.7 | 600.9 | 16,692 | 8.1 | 17.6 | 71.6 | 601.3 |
| MANYIDS (supplementary) | 16,566 | 9.8 | 16.0 | 23.1 | 86.2 | 16,700 | 8.7 | 14.6 | 21.0 | 77.9 |
| BIGIDS (run 1) | 13,455 | 113.7 | 239.8 | 321.3 | 2,596.3 | 13,675 | 106.8 | 226.2 | 300.4 | 2,532.3 |
| BIGIDS (re-run) | 13,610 | 120.9 | 245.5 | 308.2 | 1,938.8 | 13,398 | 112.7 | 231.8 | 278.0 | 1,885.2 |
| LARGE saturation (think 0) | 53,858 | 170.2 | 222.6 | 261.4 | 10,723.4 | 54,338 | 146.1 | 196.9 | 230.0 | 6,857.1 |
| BIGIDS saturation (think 0) | 15,147 | 1,050.0 | 1,251.2 | 1,461.2 | 2,919.9 | 15,022 | 158.3 | 516.6 | 614.1 | 778.1 |
| BIGIDS saturation (re-run) | 14,784 | 1,041.5 | 1,262.5 | 1,376.6 | 26,330.8 | 15,118 | 143.5 | 592.6 | 703.8 | 17,698.3 |

| Saturation (think 0) | Requests | p50 | p90 | p95 | p99 | max | Req/s | Unexpected |
|---|---|---|---|---|---|---|---|---|
| LARGE | 108,196 | 156.2 | 201.0 | 214.1 | 251.2 | 10,723.4 | 569.5 | 0 |
| BIGIDS | 30,169 | 364.9 | 1,145.9 | 1,200.9 | 1,335.0 | 2,919.9 | 158.8 | 0 |
| BIGIDS (re-run) | 29,902 | 375.6 | 1,163.1 | 1,217.1 | 1,333.2 | 26,330.8 | 157.4 | 0 |

All acceptance profiles meet the **p95 ≤ 3.0 s** target with **0 unexpected responses and 0 % failed requests**. LARGE and BIGIDS were each run twice; both runs are reported. Isolated `max` values above 1 s (SMALL 2.1 s, MEDIUM 1.3 s, BIGIDS 2.6 s and 1.9 s, saturation runs below) are single requests that leave p99 unchanged; their cause was not isolated (connection-pool growth during the ramp and garbage collection on a shared laptop are candidates, not findings).

### 13.5 Saturation (think time 0; supplementary, not the gate)

See the saturation table above. LARGE sustained about 570 requests per second with p95 214 ms. **BIGIDS saturates at about 158 requests per second** (SQL Server near 68 % of the machine, the machine near full): p95 about 1.2 s, p99 about 1.3 s, still within 3.0 s, but **first pages are about seven times slower than deep pages (p50 about 1,050 ms against about 150 ms) and a single request took 2.9 s in one run and 26.3 s in the other**. This is observation O1 in §13.9 and is recorded for the owner; no code, index or query-shape change was made.

### 13.6 BIGIDS and LARGE database evidence (per run; counters are deltas between the snapshots taken before and after the re-run)

| Run | Statement (first / later page) | Executions | Logical reads per execution | Worker time per execution | Elapsed per execution | Spill pages per execution (max) |
|---|---|---|---|---|---|---|
| LARGE re-run (think 0.5 s) | first page | 16,424 | 484 | 4.8 ms | 5.1 ms | 0 (0) |
| LARGE re-run | later page | 17,672 | 448 | 2.2 ms | 2.2 ms | 0 (0) |
| BIGIDS re-run (think 0.5 s) | first page | 13,615 | 9,785 | 23.0 ms | 45.7 ms | **8.6 (9)** |
| BIGIDS re-run | later page | 13,998 | 9,736 | 18.3 ms | 35.4 ms | 0.02 (9) |
| BIGIDS saturation re-run (think 0) | first page | 14,789 | 9,785 | 24.8 ms | **774.8 ms** | 8.6 (9) |
| BIGIDS saturation re-run | later page | 15,718 | 9,736 | 19.3 ms | 55.6 ms | 0.02 (9) |

- **Plan shape (cached plans, all four timeline statements):** no clustered-index scan and no index scan of `audit_history`; seeks on `IX_audit_history_timeline` and clustered-key lookups only; a `Sort` operator is present; no spill warning stored in the plan. No `audit_history` scan occurred under 100 concurrent callers.
- **Reads** match the Phase 1B evidence: about 9,750 logical reads for the 2,402-id Work Order at any page depth, about 450–490 for LARGE. Read volume stays independent of `pageSize` and of how many times the timeline is requested.
- **Parameters:** the same 7 (first page) and 9 (later page) as in §12.2 for every profile; the cached plans are reused (13–15 thousand executions per statement).
- **Spill:** the BIGIDS first-page statement spills about **9 pages (about 72 KB) to tempdb on every execution** (the same 50-row `OPENJSON` estimate as §12.6 item 3; the earlier note that the 500,000-row run had none applies to the smaller row count it used); the later-page statement almost never spills. With think time 0.5 s this costs no more than about 45 ms of elapsed time. At saturation, **`PAGELATCH_UP` waits on tempdb allocation pages accumulated 10.3 million ms over the run** (`SOS_SCHEDULER_YIELD` 1.7 million ms, `ASYNC_NETWORK_IO` 0.8 million ms); at think time 0.5 s the top waits were `SOS_SCHEDULER_YIELD` and `ASYNC_NETWORK_IO` and `PAGELATCH_UP` was 8 s.
- **Errors, timeouts, pool:** the API log of the whole session (4,171 lines) contains no exception, `Timeout expired`, pool-exhaustion or error-level line; sessions on the database peaked at 100 (one per virtual user) with no blocked request in any snapshot.
- **CPU and memory (machine percent, average / maximum):** think 0.5 s — LARGE `sqlservr` 11 % / 14 % (the API was not sampled in that run); BIGIDS `sqlservr` 59 % / 73 %, API 13.5 % / 17 %, k6 5 % / 6 %; saturation BIGIDS `sqlservr` 68 % / 77 %, API 15 % / 18 %. `sqlservr` working set stayed under 770 MB (the API was not sampled in the first pass because of a sampler bug fixed during this phase; the re-runs include it).

### 13.7 Negative traffic (separate run, 10 VUs, 60 s; own tags)

5,840 requests over 15 cases in rotation, **0 unexpected responses**: 401 (no token, invalid token), 403 (Technician, Administrator), generic 400 (malformed cursor, random 76-character cursor, tampered cursor, cursor bound to another Work Order, `pageSize=0`), generic 404 (missing Work Order, other Site, other tenant, Requester who is not the owner, Supervisor of another Site, unsupported `entityType`). Latency p50 2.2 ms, p95 3.6 ms, p99 6.3 ms, max 15.8 ms. Every 4xx body was `application/problem+json` and no body contained exception, stack, HMAC, table or namespace text; **no 500**. The API log carries the expected `AUTHZ_*` warnings (route template, caller id and correlation id only; no cursor, token or Work Order id): 2,339 resource-not-found, 778 access-denied, 957 unauthenticated across the session.

### 13.8 Read-only verification
- Before and after **every** run, the count and the id checksum of every non-login `audit_history` row stayed **571,225 / 702,205,577**, `@@DBTS` stayed 34,000 (nothing with a row version changed), and the Repair Request, Work Order, Visit, Session and Corrective Action counts were unchanged. The only growth was `USER` / `AUTH_LOGIN_SUCCEEDED` rows, one per login performed by a run's `setup()` (107 at the end of the session). **A timeline GET writes nothing and no read event is audited (D8).** The negative run created no audit row beyond its 10 logins.
- `verify` walked 15 timelines (first, middle and last Work Order of each profile, `pageSize=100`) through the API before the runs and again after the last run: all equal the SQL Server `ORDER BY` oracle, exactly once, newest first (25 pages for each BIGIDS timeline, 20 for LARGE).

### 13.9 Pre-commit audit of the working tree

No Critical and no Major finding. Source files were read against the checklist; behaviour was confirmed by the test suites and by the runs above.

| # | Severity | Finding | Action |
|---|---|---|---|
| A1 | Minor | `Cache-Control: no-store` is set inside the controller action, so a 401/403 produced by the authentication/authorization middleware has no `no-store` header (the body carries no data) | recorded; no change (outside D8/§4 contract text) |
| A2 | Minor | the Angular live region announces the new total after every reload; the text changes only when the count changes | recorded; no change |
| A3 | Minor | after a failed "Load more" with 403/404 the button reads "Retry" and repeats the same cursor | recorded; no change |
| O1 | Observation | BIGIDS first-page sort spill (about 9 pages) and tempdb `PAGELATCH_UP` contention under zero-think saturation; tail requests of 2.9 s and 26.3 s (§13.5, §13.6) | recorded for the owner; **not** a gate failure |
| T1 | Tooling (fixed in this phase) | k6 script `expectedStatuses` call and page-size bounds; sampler API label; snapshot `dbid` filter for ad-hoc statements | fixed in `tests/performance/` |

Confirmed in the audit: `[Authorize(Policy = Audit.TimelineRead)]` on the only action; the Work Order is found only through `IDataScope.WorkOrders` and child ids are derived on the server; the cursor tag is verified with `CryptographicOperations.FixedTimeEquals` before the version, ticks or id are read and before any database access; nothing logs a cursor, key or payload; SQL is parameterized (`OPENJSON` over real parameters; entity types are constants); one query path (two-step, separate `UNION ALL` convert branch, no fallback), ordering, comparison and `TOP(pageSize + 1)` executed by SQL Server, actor lookup tenant-scoped; 405 for other verbs, generic 404 for missing / out-of-scope / unsupported type; the Angular component cancels stale requests, guards double "Load more", de-duplicates by `auditId`, uses `<time datetime>` with a UTC title, stores nothing and renders no raw JSON or HTML; the opt-in evidence test is skipped unless `RUN_TIMELINE_EVIDENCE=1`, the default fixture seeds 100,000 rows, and the Phase 1A spike directory is gone.

### 13.10 Final regression (working tree, default settings)

| Check | Result |
|---|---|
| `dotnet build RepairRequest.slnx` | 0 warnings, 0 errors |
| Domain tests | 489 passed |
| Application tests | 507 passed |
| ApiTests | 629 passed |
| IntegrationTests | 325 passed, 1 skipped by design (the opt-in evidence test) |
| Angular tests (`npm test -- --watch=false`) | 215 passed (21 files) |
| Angular production build | passes |
| `dotnet ef migrations has-pending-model-changes` | "No changes have been made to the model since the last migration." |
| `git diff --check` | clean |

The counts equal §12.7: this phase changed no production source file. The performance tooling is not part of `RepairRequest.slnx` and is not built or run by CI.

### 13.11 Limits of this evidence
Single laptop with all three roles; SQL Server Express LocalDB (one tempdb data file); 571,225 audit rows instead of several million; 5,187 Work Orders instead of 100,000 per Site; one 2.5-minute hold per profile instead of 30 minutes; k6 closed-loop model; the API was not sampled for CPU in the first pass. The figures show that the approved two-step query meets the p95 ≤ 3.0 s target on this dataset and does not scan `audit_history`; they do not predict production capacity.
