RR-DEC-S2-001

# Repair Request — Sprint 2 / S2-001 "Work Order List" Decision Sheet

Cross-reference of `docs/03`, `docs/09`, `docs/12`, `docs/13` and the Sprint 1 implementation against the S2-001 (Work Order List) requirement, with explicit open questions for Portfolio Project Owner/SA sign-off.

## Document Control

| Field | Value |
|---|---|
| Document ID | RR-DEC-S2-001 |
| Version | 1.0 |
| Status | Resolved for S2-001 scope — open items answered by `DEC-S2-001-01..05`; see `docs/12` Section 11 |
| Revision Date | 17 September 2026 (resolution recorded same day) |
| Extends / cross-references | RR-STS-001 v1.6 (`03_...pdf`), RR-API-001 v1.2 (`09_...pdf`), RR-DEC-001 v1.2 (`12_...md`), RR-API-001-ADD v1.0 (`13_...md`), plus RR-REQ-001 v1.6 (`01_...pdf`), BR-RR-BASELINE v1.6 (`02_...pdf`), UC-RR-001 v1.7 (`04_...pdf`), RR-DD-001 v1.4 (`05_...pdf`), RR-DBD-001 v1.2 (`08_...pdf`) |
| Approval | Portfolio Project Owner Approval, 17 September 2026 — `DEC-S2-001-01..05` in `docs/12` Section 11 |
| Audience | Portfolio Project Owner, SA, Backend/Frontend Developer |
| Purpose | Establish exactly what S2-001 "Work Order List" can be built from the existing baseline without guessing a business rule, and enumerate precisely what still needs a decision before backend/frontend work starts. |

**Revision rule (same as RR-DEC-001 §"Revision rule"):** This document does not itself change any locked business semantics. No PDF baseline document (`docs/01`–`docs/11`) is modified. Nothing here invents a business rule not already stated by a cited source; every open question is marked **ต้องตัดสินใจ** rather than answered with an assumption.

**Scope note:** This sheet covers S2-001 (Work Order List) only. It confirms, per the prior blocker report (this session, not yet filed), that no `WorkOrder` entity, table, policy, or endpoint exists in the codebase yet — everything here is preparatory analysis for backend/frontend work that has not started.

---

## 1. WO creation trigger and Repair Request : Work Order cardinality

**ข้อกำหนดที่ยืนยันได้:**
- `UC-WO-001 — Create Work Order from Approved Request` (`docs/04_Repair_Request_Use_Case_Specification_v1.7_Performance_Revision.pdf`, p.4/14): Primary Actor **Coordinator**; Trigger *"Coordinator chooses Convert"*; State Flow *"APPROVED -> CONVERTED; WO OPEN"*; Preconditions *"Request APPROVED; no existing WO"*; Main Flow *"Validate state/version; create WO + link; Request CONVERTED atomically."*; Postcondition *"Exactly one WO OPEN."*; Traceability `FR-04; BR-03; ST-RR-008`.
- `ST-RR-008` (`docs/03_Repair_Request_State_Transition_v1.6_Performance_Reviewed.pdf`, §2 "Repair Request Transition Matrix", p.2/5): From `APPROVED`, Action `Create Work Order`, Actor `Coordinator`, Guard *"No existing WO; current rowversion"*, To `CONVERTED`, Side Effect *"Atomically create WO OPEN; audit/outbox"*. The Work Order Transition Matrix's own first row, `ST-WO-001` (`docs/03` §3, p.2/5), is the transition **out of** `OPEN` (`From: OPEN, Action: Schedule, Actor: Coordinator`) — confirming `OPEN` is established as a side effect of `ST-RR-008`, not by a separate WO-initiated transition.
- `BR-03` (`docs/02_Repair_Request_Business_Rules_Baseline_v1.6_Performance_Reviewed.pdf`, §2, p.2/4; mirrored in `docs/01_Repair_Request_Requirement_and_Scope_v1.6_Performance_Revision.pdf`, §6, p.3/8): *"Create Work Order only from APPROVED; exactly one Work Order per Repair Request; Request becomes CONVERTED."*
- `WO-004` (`docs/05_Repair_Request_Data_Dictionary_v1.4_Performance_Revision.pdf`, Work Order table, p.4/10): `repair_request_id | UUID | Y | "Unique FK; source APPROVED"`. ERD (`docs/05` §3, p.2/10) shows `work_order.repair_request_id` as `UQ` with a `0..1` cardinality annotation on the relationship.
- Current implementation: `RepairRequest.cs` (`backend/src/RepairRequest.Domain/RepairRequests/RepairRequest.cs:6-11`) class doc: *"CONVERTED (ST-RR-008) exists for lifecycle compatibility but Work Order creation is Sprint 2 scope."* — confirms `CONVERTED` is a defined enum value with no implemented transition logic yet.

**ข้อที่เอกสารขัดกันหรือยังไม่กำหนด:** None. This is the most completely and consistently specified area across all five documents. No mention anywhere (searched `docs/01`–`docs/05`) of re-conversion, a second Work Order per Repair Request, or any exception to the 1:1 rule.

**ข้อเสนอที่เปลี่ยน business rule น้อยที่สุด:** Implement `WorkOrder.RepairRequestId` as a unique FK exactly as `WO-004` specifies, with `Status` initialized to `OPEN` only via a future `Convert` command (out of S2-001 scope — S2-001 is read-only). No proposal needed beyond faithful implementation; there is no gap to fill here.

**ผลกระทบ:**
- **Entity:** `WorkOrder.RepairRequestId` (Guid, unique constraint), `WorkOrder.Status` enum starting at `OPEN`.
- **API DTO:** Repair Request No. can be joined via a plain 1:1 `INNER JOIN`/navigation with zero fan-out risk — no de-duplication or "which RR" ambiguity ever arises for a WO list row.
- **Authorization:** `Convert` needs its own future policy (e.g. `WorkOrder.Convert`, Coordinator-only) — explicitly not part of S2-001, flagged here only so the prerequisite backend ticket accounts for it.
- **Angular list:** No special-casing needed for the Repair Request No. column; it is always exactly one value.

---

## 2. Can `customerCode` stand in for Customer name?

**ข้อกำหนดที่ยืนยันได้:**
- `Customer` domain entity, full file (`backend/src/RepairRequest.Domain/MasterData/Customer.cs:1-26`): the only business property is `CustomerCode` (string). Class doc: *"Customer master (DEC-PS1-001). Belongs to a Tenant; customer_code is unique within the Tenant (DEC-PS1-015)."*
- `CustomerResponse` API DTO (`backend/src/RepairRequest.Api/Contracts/MasterData/MasterDataContracts.cs:28`): `record CustomerResponse(Guid Id, string CustomerCode, string Status, string? DeactivateReason, string RowVersion)` — no name field is returned by the implemented Customers API today.
- `DEC-PS1-001` (`docs/12_Pre_Sprint1_Baseline_Decision_Register_v1.0.md`, lines 38-58): *"Customer is a full managed master-data entity in Sprint 1... Current baseline state: No standalone Customer entity exists anywhere in the baseline. 'Customer/Company' currently appears only as tenant_id..."* and explicitly: *"Open modeling question (must not be assumed): ... is one Customer record synonymous with one tenant (1:1), or can a single tenant contain multiple Customer records? The most consistent reading with D-11 is 1:1 (Customer is the tenant boundary), but this must be confirmed by the Business Owner/SA before the Data Dictionary/ERD is authored — guessing this would risk inventing a business rule."*

**ข้อที่เอกสารขัดกันหรือยังไม่กำหนด:** **ต้องตัดสินใจ.** No display-name field for Customer exists anywhere in the frozen baseline (`docs/01`–`docs/11`) or in the Sprint 1 implementation — `Customer` as a formal entity is itself a Sprint-1-only addition (`DEC-PS1-001`) whose own Customer:Tenant cardinality is still an open question per that same decision. There is no basis in any document to invent a "Customer name" field for S2-001.

**ข้อเสนอที่เปลี่ยน business rule น้อยที่สุด:** Show `customerCode` in the list, but label the column **"Customer Code"**, not "Customer" — this makes zero schema change, adds zero new fields, and doesn't imply the UI is showing a proper name that doesn't exist.

**ผลกระทบ:**
- **Entity:** None — no change to `Customer`.
- **API DTO:** Work Order list DTO carries `customerCode` (string) sourced via `WorkOrder → RepairRequest → Site → Customer`.
- **Authorization:** None.
- **Angular list:** Column header reads "Customer Code"; do not render it as if it were a company display name. Flag to Portfolio Project Owner/SA (Section 6) that adding a real Customer name field is a separate, un-scoped future decision.

---

## 3. Summarizing Scheduled Date / Assigned Technician across 0..N Service Visits

**ข้อกำหนดที่ยืนยันได้:**
- `SV-003 work_order_id` (`docs/05` Service Visit table, p.5/10): a plain FK, **not** unique — confirms **Work Order : Service Visit = 1 : many**. Full Service Visit field set: `SV-004 visit_type` (INITIAL/FOLLOW_UP/CORRECTIVE), `SV-005 status` (SCHEDULED/RESCHEDULED/IN_PROGRESS/COMPLETED/MISSED/CANCELLED), `SV-006 assigned_team_id`, `SV-007 assigned_technician_id` (both conditionally required), `SV-008/009 scheduled_start_at/scheduled_end_at` (required `@Scheduled`), `SV-016 source_missed_visit_id` ("New visit link to original MISSED; original remains MISSED").
- Service Visit Transition Matrix `ST-SV-001..009` (`docs/03` §4, p.3-4/5) — full lifecycle: Create Scheduled Visit, Check-in, Check-out, Reschedule, Confirm Schedule, Reassign, Cancel Visit, Mark Missed, Decide Missed.
- `D-15` — MISSED Decision Semantics, identical text in `docs/02` §1 (p.2/4) and `docs/01` §7 (p.3/8): *"Original MISSED Service Visit remains immutable. Reschedule/Follow-up/Reassign creates a new SCHEDULED visit referencing the MISSED visit. Cancel decision records 'no follow-up'; no new visit is created and the Work Order continues unless separately cancelled."* Also referenced in the Action-level Permission table (`docs/01` §13, p.6/8): *"Reassign/Reschedule/MISSED decision — Coordinator; reason; D-15 for MISSED."*
- `UC-WO-002 — View/Track Work Order, Timeline and Audit` (`docs/04`, p.5/14): Main Flow *"Search/list/detail/timeline with server-side filters/paging."* Traceability `FR-09; BR-02/16`. `UC-REP-001` (`docs/04`, p.13/14) is the same shape: *"Server-side filters/paging; timeline; report areas; export with scope/PII filtering."* (`FR-09; BR-02/16/18`).

**ข้อที่เอกสารขัดกันหรือยังไม่กำหนด:** **ต้องตัดสินใจ.** An exhaustive, case-insensitive search across `docs/01`–`docs/05` for "current visit", "most recent", "latest", "next upcoming", "representative", "active visit", "primary visit" found **no** matching business rule anywhere. `UC-WO-002`/`UC-REP-001` describe a generic filterable/paged list but never say which of a WO's 0..N Service Visits (including a possibly-immutable MISSED one per `D-15`) should populate a single "Scheduled Date"/"Technician" cell. This is a genuine specification gap, not an oversight of this review.

**ข้อเสนอที่เปลี่ยน business rule น้อยที่สุด (conservative — does not invent a tie-break rule):** Do not silently encode a selection rule (e.g. `MAX(scheduled_start_at) WHERE status <> CANCELLED`) as if it were approved business logic. Instead:
- Render Scheduled Date / Technician only when the Work Order has **exactly one** non-CANCELLED Service Visit.
- Otherwise render an explicit ambiguous state ("—" / "Multiple visits — see detail" / "No visit scheduled") rather than guessing.
- Document a mechanical fallback (latest non-CANCELLED `scheduled_start_at`) as an *alternative* the Portfolio Project Owner/SA can approve instead, if an always-populated column is preferred over an honest "ambiguous" state.

**ผลกระทบ:**
- **Entity:** Requires a `ServiceVisit` entity to exist at all — not built yet (separate prerequisite, same as `WorkOrder` itself).
- **API DTO:** `scheduledAt` / `technicianName` (or similar) fields must be nullable/optional, never assumed present.
- **Authorization:** If a Technician's name is shown, the WO list query must not leak visit-assignment information outside the viewer's own data scope — ties directly to Section 4 below.
- **Angular list:** The Scheduled Date/Technician column must have a defined rendering for 0, 1, and >1 visits; it cannot assume a single value is always available.

---

## 4. Per-role Work Order read access and row-level data scope

**ข้อกำหนดที่ยืนยันได้:**
- `docs/12` §8 "Deferred Items", line 877: *"Sprint 2 team / assignment scope (Work Order, Visit, Session policies) | DEFERRED — Sprint 2 | `AuthorizationPolicies` note; RR-REQ-001 §13"*.
- `AuthorizationPolicies.cs:8` (`backend/src/RepairRequest.Application/Security/AuthorizationPolicies.cs`): *"Work Order / Visit / Session policies belong to Sprint 2."*
- `UC-WO-002` (`docs/04`, p.5/14) Precondition: *"Within tenant/site/role/ownership scope."* — no further detail.
- `BR-02` (`docs/02` §2, p.2/4): *"Any significant action / all roles — Server-side mandatory/master/role/scope/state/concurrency validation — Reject invalid action; no state change/partial write."* `BR-16` (`docs/02` §2, p.3/4): *"Identity/Scope mapping — Authenticated context — created_by=Requester; tenant_id=Customer/Company scope; never client authority."* Both cited directly in `UC-WO-002`'s and `UC-REP-001`'s traceability tags (`BR-02/16`), i.e. they govern read/list access generally but specify no WO-specific row-level rule.
- Existing implemented pattern to mirror: `IDataScope.RepairRequests(CurrentUser user)` (`backend/src/RepairRequest.Application/Security/IDataScope.cs:34-39`; implementation `backend/src/RepairRequest.Infrastructure/Authorization/DataScope.cs:92-123`) — filters by `TenantId`; `!user.HasBusinessRole` (Administrator) → empty result; `HasSiteWideRequestScope` roles see every request at their `UserSiteScope`-assigned Sites; Requester sees only requests they created (including Site-less drafts).
- `AuthorizationPolicies.RepairRequestRead` (`AuthorizationPolicies.cs:38-46`) allows Requester, Approver, Coordinator, Technician, TeamLead, Supervisor — Administrator excluded by design ("ADMINISTRATOR alone does not impersonate a business actor", `DataScope.cs:100`).
- `RoleCodes.cs` (`backend/src/RepairRequest.Domain/Security/RoleCodes.cs:11-17`): seven roles total — `REQUESTER, APPROVER, COORDINATOR, TECHNICIAN, TEAM_LEAD, SUPERVISOR, ADMINISTRATOR`.

**ข้อที่เอกสารขัดกันหรือยังไม่กำหนด:** **ต้องตัดสินใจ.** No document assigns which of the 7 roles should see which Work Orders, nor any row-level rule (e.g., should Technician see only WOs containing a Visit assigned to them, once Service Visit exists? Does Coordinator see all WOs at their scoped Sites the same way they'd see Repair Requests?). This is explicitly and only "DEFERRED — Sprint 2" with no further breakdown anywhere in the baseline.

**ข้อเสนอที่เปลี่ยน business rule น้อยที่สุด:** Mirror `RepairRequest.Read` / `IDataScope.RepairRequests` exactly, since every Work Order originates 1:1 from a Repair Request the same six roles can already read (Section 1 above):
- New `WorkOrder.Read` policy = same six roles as `RepairRequest.Read` (Requester, Approver, Coordinator, Technician, TeamLead, Supervisor; Administrator excluded, consistent with the existing no-business-read rule).
- New `IDataScope.WorkOrders(CurrentUser user)` mirroring the identical shape: site-wide roles see WOs whose linked Repair Request's `SiteId` is in the caller's `UserSiteScope`; Requester sees only WOs from Repair Requests they created.
This is presented as a proposal for Portfolio Project Owner/SA sign-off — not a confirmed rule — using the same treatment `DEC-PS1-001` gave the open Customer-cardinality question.

**ผลกระทบ:**
- **Entity:** None beyond `WorkOrder` itself.
- **API DTO:** None.
- **Authorization:** New `WorkOrder.Read` policy entry in `AuthorizationPolicies.cs` (same dictionary pattern, lines 33-50); new `WorkOrders(CurrentUser)` method in `IDataScope`/`DataScope.cs` (same `IQueryable` composition pattern, no in-memory materialization).
- **Angular list:** This would be the first Angular authorization guard in the codebase — none exist yet (`frontend/src/app/{core,shared}` are empty scaffolding). Needs its own small design decision (e.g. a route guard reading claims from an auth service) once the backend policy exists; out of scope to design further until Section 6 Q3 is answered.

---

## 5. Shape of `GET /api/v1/work-orders` (filters, sort, paging, DTO)

**ข้อกำหนดที่ยืนยันได้:**
- `docs/09_Repair_Request_API_Contract_v1.2_Performance_Revision.pdf` §3 "Endpoint Catalog" (p.3-4) defines **only** `WO-API-001 | GET | /api/v1/work-orders/{id} | WO detail | Authorized | -` for Work Order GETs. No `WO-API-000` or bare `/api/v1/work-orders` (collection) row exists anywhere in the catalog. Every other `WO-*`/`ACC-*`/`CST-*` route also carries `{id}`.
- `WorkOrderSummary` DTO (`docs/09` §6, p.5): `workOrderId, workOrderNo, repairRequestId, status, ownerTeamId, teamLeadId, sla summary, rowVersion` — field names only, no types; missing Customer/Site/Equipment/Repair Request No. (display)/Scheduled Date entirely.
- `docs/09` §7 "Search / Pagination / Reporting / Files" (p.5, full section): *"List/report APIs apply tenant/site scope server-side. page/pageSize/sort are technical conventions; exact max page size is deployment config. Export uses the same scope/PII rules. Files never return public storage URLs."* §9 Performance appendix, "List/Search" row (p.6): *"Tenant/site scope is applied server-side before paging. Query DTO/OpenAPI documents supported filter/sort fields and paging conventions... List endpoints use focused summary DTOs/projections; do not serialize full aggregate graphs for simple list screens."*
- `docs/13_Repair_Request_API_Contract_Addendum_v1.0.md` §1: `RR-API-003 | GET /api/v1/repair-requests | NOT IMPLEMENTED`; and the consolidated row `| WO-*, WS-*, ACC-*, CA-*, CST-*, TIME-*, SLA-*, AUD-*, REP-*, NTF-* | — | NOT IMPLEMENTED (later sprints / tickets) |` — i.e. even the closest analogous list endpoint (Repair Request's own) is unimplemented, and all WO-* endpoints (including the one detail GET) are unimplemented.
- `docs/13` §3.4 "Paging" (the actually-implemented convention already used by Customers/Sites/Equipment/Routing Issues): *"Query string: page (1-based) and pageSize... pageSize defaults to Paging:DefaultPageSize and is clamped to Paging:MaxPageSize. Current appsettings: 20 / 100... Response body: { items, page, pageSize, totalCount }. Tenant/site scope is applied before paging, and paging runs in SQL."* §3.2 "Error contract": standard `application/problem+json` with `code`/`correlationId`, HTTP 400/401/403/404/409/422 as documented.
- Codebase template — `RoutingRecoveryController.ListIssues` (`backend/src/RepairRequest.Api/Controllers/RoutingRecoveryController.cs:32-45`): `[HttpGet("api/v1/routing-issues")]`, `[Authorize(Policy = AuthorizationPolicies.RoutingRecovery)]`, `[FromQuery] RoutingIssueListRequest request` (just `Page`/`PageSize`, `ApprovalRoutingContracts.cs:26-32`), `PageRequests.TryCreate(...)` validates/clamps, returns `Ok(new PagedResponse<T>(items, page, pageSize, totalCount))`. Shared primitives in `backend/src/RepairRequest.Api/Http/PagingOptions.cs:9,15-46,52-59` (`PagedResponse<T>`, `PageRequests.TryCreate`, `PagingOptions`).
- `docs/08_Repair_Request_Database_Design_v1.2_Performance_Revision.pdf` §5 "Index / Performance Strategy" (p.3-5): "Request List/Search" → `tenant_id + site_id + status + submitted_at (+ category/priority/request_no as needed)`; "Technician schedule" → `assigned_technician_id + status + scheduled_start_at` (on `service_visit`, per schema overview p.2-3). **No "Work Order list" row exists** in this table at all.

**ข้อที่เอกสารขัดกันหรือยังไม่กำหนด:** **ต้องตัดสินใจ.** The baseline API contract has zero definition of a Work Order list/search endpoint — no route, no filter fields, no sort fields, not even a reserved ID. `UI-030 "Work Order List"` is named in the screen inventory (`docs/10` §2) but has no wireframe, unlike Repair Request's `UI-010`. Which filters (status? Site? team? date range?) are wanted cannot be derived from any document.

**ข้อเสนอที่เปลี่ยน business rule น้อยที่สุด:** Reuse the implemented pattern verbatim rather than inventing new conventions:
- Route: `GET /api/v1/work-orders` (the base path is already reserved by `WO-API-001`'s `/api/v1/work-orders/{id}`).
- Query params for MVP: `page`, `pageSize` only — matching `RoutingIssueListRequest`'s minimal shape exactly. No filters in the first cut, since none are documented or wireframed; treat `status`/`siteId` filters as a fast-follow once Portfolio Project Owner/SA confirms which are wanted (candidate shape borrowed from `docs/08`'s "Request List/Search" index: `tenant_id + site_id + status`).
- Response: `PagedResponse<WorkOrderListItemResponse>(Items, Page, PageSize, TotalCount)` — identical envelope already used by Customers/Sites/Equipment/routing-issues.
- DTO: extend `WorkOrderSummary`'s documented fields (`workOrderId, workOrderNo, repairRequestId, status, ownerTeamId, teamLeadId, rowVersion`) additively with the display joins S2-001 needs — `repairRequestNo, siteId, equipmentId, customerCode` — since these are pure projections via existing 1:1/FK relationships, not new business rules. `scheduledAt`/`technician` per Section 3 must be nullable pending that decision.
- Errors/concurrency: reuse `docs/13` §3.2 verbatim; no `If-Match` needed (list is a GET, matching `WO-API-001`'s own "-" concurrency marker).

**ผลกระทบ:**
- **Entity:** None beyond the not-yet-built `WorkOrder` entity itself (already flagged as a prerequisite in the prior blocker report).
- **API DTO:** New `WorkOrderListItemResponse` (extends `WorkOrderSummary` fields + display joins) and `WorkOrderListRequest { Page, PageSize }`.
- **Authorization:** `[Authorize(Policy = AuthorizationPolicies.WorkOrderRead)]`, per Section 4.
- **Angular list:** Table columns map 1:1 to `WorkOrderListItemResponse` fields; pagination UI follows the same `{items, page, pageSize, totalCount}` envelope already used elsewhere in this API — the first Angular implementation of the pattern, but the shape itself is stable and precedented, not novel.

---

## 6. Consolidated open questions — require Portfolio Project Owner/SA decision before implementation

1. **Customer display** (Section 2): Should "Customer" ever show a real name, or is `customerCode` acceptable long-term? This is entangled with `DEC-PS1-001`'s still-open Customer:Tenant 1:1 question.
2. **Scheduled Date / Technician summarization** (Section 3): When a Work Order has 0, 1, or many Service Visits (including an immutable MISSED one per `D-15`), which value — if any — should populate the list row? Approve either the conservative "ambiguous state" proposal or the mechanical latest-non-cancelled-visit fallback.
3. **Row-level data scope** (Section 4): Confirm whether mirroring `RepairRequest.Read`/`IDataScope.RepairRequests` exactly is acceptable, or whether e.g. Technician needs a narrower "my assigned visits only" scope once Service Visit exists.
4. **List endpoint filters** (Section 5): Beyond `page`/`pageSize`, which filter/sort fields (if any) should `GET /api/v1/work-orders` support for the MVP?

No backend or frontend implementation should proceed on the items above until answered; everything else in Sections 1–5 is already fully supported by cited baseline text and can be implemented as stated once the `WorkOrder`/`ServiceVisit` entities themselves are built (separate prerequisite, not part of this sheet).

**Status update — 17 September 2026:** Items 1–4 above are **RESOLVED for S2-001 scope** by the Portfolio Project Owner; see `DEC-S2-001-01..05` in `docs/12_Pre_Sprint1_Baseline_Decision_Register_v1.0.md` Section 11. Summary: Customer/Site/Equipment show as code only (item 1) — `DEC-PS1-001`'s Customer:Tenant cardinality question remains explicitly open. Scheduled Date/Technician stay hidden pending the Schedule ticket (item 2). Work Order read scope mirrors Repair Request's, with TECHNICIAN excluded and Team/Team Lead display also omitted since no Team master-data entity exists (item 3). The list endpoint ships with a `status` filter, `page`/`pageSize` paging and a fixed newest-first sort backed by a new technical `created_at` column (item 4).
