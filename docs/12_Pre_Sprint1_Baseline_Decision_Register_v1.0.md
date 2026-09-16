RR-DEC-001

# Repair Request — Pre-Sprint-1 Baseline Decision Register

Portfolio Implementation Decisions + Cross-Reference + Required Baseline Updates

## Document Control

| Field | Value |
|---|---|
| Document ID | RR-DEC-001 |
| Version | 1.2 – Sprint 1 Implementation Decisions + Pre-S1-007 Resolution |
| Status | Approved for Portfolio Development |
| Revision Date | 15 September 2026 |
| Supersedes | RR-DEC-001 v1.1 — v1.2 adds Sections 6–10 (S1-004 D1–D4, S1-005 E1–E3, S1-006 F1–F4 + storage-key and attachment-listing decisions, Pre-S1-007 decisions DEC-PRE-S1-007-01..12, deferred/non-blocking register, traceability). No v1.1 decision is removed or changed. |
| Revision History | v1.0 — Round 1 (DEC-PS1-001..005). v1.1 — Round 2 (DEC-PS1-013..016, PERF-DEC-01). v1.2 — 14 September 2026, documentation reconciliation before S1-007 (commit a10cab8). v1.2 amendment — 14 September 2026: final S1-007-start decision — FAILED attachments do not block Submit (DEC-PRE-S1-007-07); NB-5 resolved. v1.2 amendment — 14 September 2026 (S1-007 pre-commit review): ACTIVE contact portion recorded as DEFERRED with the enforced contact rules (DEC-PRE-S1-007-04, NB-10); DEC-PRE-S1-007-08 implemented as Development/Testing-only test infrastructure; DEC-S1-006-F2 clarified. v1.2 amendment — 15 September 2026 (S1-007 pre-commit review, Portfolio Project Owner): RR-018 request-contact ACTIVE-user check formally deferred, existence + tenant + Site-scope validation remains mandatory (DEC-PRE-S1-007-04, NB-10); follow-up requirement REQ-FU-USR-001 User lifecycle/status added (Section 8.1). v1.2 amendment — 15 September 2026 (S1-007 pre-commit review, documentation reconciliation): REQ-FU-USR-001 wording made explicit; S1-007 automated evidence recorded in Section 10; RR-API-001-ADD reconciled with the implemented S1-007 contract. No decision changed. v1.2 amendment — 15 September 2026 (S1-007 pre-commit review, MUST FIX): the BR-14 duplicate check made concurrency-safe. The duplicate count and Submit now run in one transaction, serialized per duplicate key by a SQL Server application lock (implementation note under DEC-PRE-S1-007-09). Business behaviour is unchanged. v1.2 amendment — 15 September 2026 (S1-008 / S1-007R pre-implementation reviews, Portfolio Project Owner): S1-008 blocked on the baseline assigned-approver rule; routing prerequisite S1-007R decided and implemented (Section 7R, DEC-PRE-S1-007R-01..10); segregation-of-duties rule for Approve/Reject approved (DEC-PRE-S1-008-01); Return for Correction confirmed as a separate ticket (DEC-PRE-S1-008-02); Section 8 routing row updated with new deferrals; Section 10 traceability added. v1.2 amendment — 15 September 2026 (S1-007R pre-commit review, Portfolio Project Owner): approval_route_step key and approval → step foreign key made tenant-composite so no redundant foreign-key indexes exist; approval inbox index deferred to the approval inbox ticket (DEC-PRE-S1-007R-06 persistence note, Section 8); two routing tests added (Submit routing vs Admin Retry race, named approver without APPROVER role); ADD §4.1/§4.6 wording reconciled (nullable lastRoutingFailureCode). No business decision changed. v1.2 amendment — 15 September 2026 (S1-007R pre-commit review, Portfolio Project Owner): a committed Submit is never reported as failed when post-commit routing or read-back fails (DEC-PRE-S1-007R-01 post-commit response rule); routing audit actor set to System with the initiating user kept as initiatedBy (DEC-PRE-S1-007R-01 audit actor); a route-level failure removes an earlier unassigned failure row (DEC-PRE-S1-007R-09 current-failure semantics); tenant-composite foreign keys proven by model and database tests; Admin Retry routing lock given a bounded wait that returns 409 instead of an uncontrolled failure (DEC-PRE-S1-007R-07 lock wait). v1.2 amendment — 16 September 2026 (S1-008 pre-implementation review, Portfolio Project Owner): Approve/Reject source state UNDER_REVIEW only, SUBMITTED → 409 (DEC-PRE-S1-008-03); S1-008 Approve/Reject implemented (Section 8 row, Section 10 traceability); decision SLA stop and notifications recorded as deferred. v1.2 amendment — 16 September 2026 (S1-009 pre-implementation review, Portfolio Project Owner): Cancel leaves approval rows unchanged (DEC-PRE-S1-009-01); S1-009 Cancel implemented (Section 8, Section 10); SLA stop REQUEST_CANCELLED and NTF-CANCEL recorded as deferred. |
| Companion Documents (v1.2) | RR-API-001-ADD v1.0 (`13_Repair_Request_API_Contract_Addendum_v1.0.md`) — implemented endpoints missing from RR-API-001 v1.2, plus the Pre-S1-007 contract amendments, implemented in S1-007 (commit 36ffc6a) |
| Extends | RR-REV-001 v1.2, RR-REQ-001 v1.6, BR-RR-BASELINE v1.6, RR-STS-001 v1.6, UC-RR-001 v1.7, RR-DD-001 v1.4, RR-DBD-001 v1.2, RR-API-001 v1.2, RR-UI-001 v1.2, RR-TC-001 v1.3, RR-ARCH-001 v1.1, RR-PERF-001 v1.0 |
| Approval | Portfolio Project Owner — Approved for Portfolio Development |
| Approval Type | Portfolio Project Owner Approval |
| External Business Owner | N/A — Portfolio Project |
| External Project Manager | N/A — Portfolio Project |
| Development Status | Approved for Portfolio Development |
| Sources | All approved DEC-PS1 decisions recorded by this register (Section 1) together with the Pre-Sprint-1 Baseline Cleanup and Closure audits that preceded and accompany them |
| Audience | Portfolio Project Owner, SA, Backend/Frontend Developer, DBA, QA, Security Reviewer |
| Purpose | This is the **authoritative Pre-Sprint-1 decision register and baseline-amendment reference** for the Repair Request project: it records every approved DEC-PS1 portfolio decision, cross-references each against the existing baseline, and specifies exactly which baseline documents/sections require new content or wording updates to reflect them. |

**Approval model note:** This is a self-directed portfolio project, not a client engagement. There is no external Business Owner or Project Manager. Baseline decisions in this register (and in the documents it extends) are approved under **Portfolio Project Owner Approval** — the portfolio owner is the governance authority for this project. No external name, signature, or date is fabricated anywhere in this register or in any baseline document; where the original PDF baseline text says "Business Owner / Project Manager — formal name/signature pending," that phrasing is inherited from the source document template and is superseded, for governance-classification purposes, by this approval model. The absence of an *external* Business Owner/PM is therefore **INFORMATIONAL**, not a WARNING or BLOCKER (see Section 4, item 7; formally recorded as DEC-PS1-016).

**Revision rule:** This register does not itself change any locked business semantics (State, Role, Guard, SLA, Permission, Notification Recipient). Where a decision below requires new entity fields, state models, or API shapes that do not yet exist in the baseline, this register marks them as **REQUIRED NEW CONTENT — pending Portfolio Project Owner/SA authoring**, not as already-approved detail. No business rule, field name, or validation wording is invented here beyond what each decision statement itself specifies; anything not explicitly stated by the decision is flagged as an open question rather than assumed.

**Scope note:** No PDF baseline document is modified by this register. This is a new companion document; the existing PDFs remain as last approved/reviewed.

---

## 1. Decision Register

### DEC-PS1-001 — Customer Master

> Customer is a full managed master-data entity in Sprint 1. Sprint 1 must support authorized Create, Read, Update, Activate/Deactivate operations according to role and company data scope.

**Current baseline state:** No standalone Customer entity exists anywhere in the baseline. "Customer/Company" currently appears only as **`tenant_id`**, a server-derived security/business scope value (D-11, RR-REQ-001 §7; RR-DD-001 §1, field `RR-002`). There is no Customer table, no Customer fields, no Customer state machine, no Customer use case, no Customer API, no Customer screen, and no Customer test case.

| Baseline area | Current coverage | Gap |
|---|---|---|
| Requirement | D-11 defines `tenant_id` = Customer/Company scope; Administrator responsibility mentions "Master data... configuration" generically; no FR for Customer CRUD; Scope Boundary does not list Customer management | **New FR required**; Scope Boundary update |
| Business Rules | None specific to Customer CRUD | **New BR(s) required** (create/edit guard, activation guard, company-scope enforcement) |
| State Transition | No Customer state model exists | **New state model required**: e.g. `ACTIVE` / `INACTIVE`, transition IDs `ST-CUST-00x` |
| Use Case | None | **New Use Cases required**: Create, Edit, Activate, Deactivate |
| Data Dictionary | None — RR-DD-001 §2 explicitly treats master data as "not hardcoded" / reference-only | **New entity table required**: fields, PK, validation, audit |
| Database Design | None | **New table + constraints + indexes required** |
| API Contract | None | **New endpoints required**: `POST/GET/PATCH /api/v1/customers`, activate/deactivate actions |
| UI Flow | None — RR-UI-001 §9 explicitly lists "Master-data CRUD details" as **Deferred to Master Data extension** | **New screens required**; §9 row must change from Deferred to In-Scope |
| Test Cases | None | **New TC-CUST-\* cases required** + UAT scenario |

**Open modeling question (must not be assumed):** RR-REQ-001 D-11 defines `tenant_id` as *the* representation of Customer/Company scope, i.e. every business row already carries `tenant_id` as its Customer scope key. DEC-PS1-001 now asks for Customer as a *manageable entity*. This raises a question the decision text does not answer: **is one Customer record synonymous with one tenant (1:1), or can a single tenant contain multiple Customer records?** The most consistent reading with D-11 is 1:1 (Customer *is* the tenant boundary), but this must be confirmed by the Business Owner/SA before the Data Dictionary/ERD is authored — guessing this would risk inventing a business rule.

---

### DEC-PS1-002 — Site Master

> Site is a full managed master-data entity in Sprint 1. Site belongs to Customer/Company scope and must support authorized Create, Read, Update, Activate/Deactivate operations.

**Current baseline state:** Site is referenced everywhere as `site_id` (RR-DD-001 field `RR-005`, `ARC-004`, `SV-*`, etc.) and as an "active master" (RR-REQ-001 §12 Data Mapping), but — identically to Customer — has **no owning entity definition**. RR-ERD-001 §1 explicitly states the baseline "does not invent master-data internal schemas" for Site.

| Baseline area | Gap |
|---|---|
| Requirement | New FR for Site CRUD; Scope Boundary update |
| Business Rules | New BR(s): Site must belong to an **active** Customer (guard on create); activation/deactivation guard + company scope enforcement |
| State Transition | New state model (`ACTIVE`/`INACTIVE`), transition IDs `ST-SITE-00x` |
| Use Case | New UCs: Create, Edit, Activate, Deactivate (parented by Customer) |
| Data Dictionary | New entity table: fields, PK, FK to Customer, validation, audit |
| Database Design | New table + FK constraint to Customer + indexes |
| API Contract | New endpoints: `POST/GET/PATCH /api/v1/customers/{customerId}/sites` (or equivalent), activate/deactivate |
| UI Flow | New screens; §9 row update (same as DEC-PS1-001) |
| Test Cases | New TC-SITE-\* cases, including a case for "create Site under inactive Customer → denied" |

**Dependency:** Site's exact `customer_id`/`tenant_id` relationship depends on resolving the DEC-PS1-001 open modeling question above (whether Customer ≡ tenant 1:1).

---

### DEC-PS1-003 — Equipment Master

> Equipment is a full managed master-data entity in Sprint 1. Equipment must belong to a Site and must support authorized Create, Read, Update, Activate/Deactivate operations. Inactive reason is required when applicable according to existing business rules.

**Current baseline state:** Equipment is referenced only as `equipment_id` (RR-DD-001 field `RR-006`), conditionally required/validated by Category (RR-REQ-001 §12: "category decides conditional Equipment/Location and evidence policy"). No owning entity definition exists.

| Baseline area | Gap |
|---|---|
| Requirement | New FR for Equipment CRUD; Scope Boundary update |
| Business Rules | New BR(s): Equipment must belong to an active Site; **Deactivate requires reason**, following the existing reason-required pattern already established by **BR-04** ("Reject/Cancel/Reassign/Reschedule/Override/Correction require reason and audit") — this decision explicitly directs reuse of that existing convention, not invention of a new one |
| State Transition | New state model (`ACTIVE`/`INACTIVE`), transition IDs `ST-EQP-00x`; Deactivate transition requires `reason` field per BR-04 pattern |
| Use Case | New UCs: Create, Edit, Activate, Deactivate(reason) |
| Data Dictionary | New entity table: fields, PK, FK to Site, `deactivate_reason` field (nullable, required when transitioning to INACTIVE), audit |
| Database Design | New table + FK constraint to Site + indexes |
| API Contract | New endpoints: `POST/GET/PATCH /api/v1/sites/{siteId}/equipment`, deactivate (reason required in body, mirroring existing reason-required command shapes such as `POST .../reject`) |
| UI Flow | New screens; §9 row update; Deactivate dialog requires reason input (same UX pattern already specified in RR-UI-001 §6.1 "Confirmation dialogs" for Cancel/Reject) |
| Test Cases | New TC-EQP-\* cases, including "deactivate without reason → denied" mirroring existing TC-RR-007/009 pattern |

**Traceability note:** Unlike DEC-PS1-001/002, this decision's "reason required" clause is **not** a new invented rule — it explicitly instructs reuse of the already-approved BR-04 reason/audit convention. This is the one piece of new-entity business logic that is already fully specified by cross-reference to an existing rule.

---

### DEC-PS1-004 — Authentication Architecture

> Use ASP.NET Core Identity as the application identity store. JWT access token + refresh token + ASP.NET Core Identity password hashing + role/permission authorization + company data scope + site data scope + token revocation/rotation where required. Do not introduce Auth0, Firebase Authentication, Entra ID, or another external Identity Provider unless a future approved change requires it.

**Current baseline state — direct conflict found.** RR-ARCH-001 v1.1 **already recorded a different, contradictory decision** that has not yet been superseded:

- §4 "Technology and Platform Decisions" — Auth standard row: *"OpenID Connect / OAuth 2.x Authorization Code + PKCE for web client; API bearer access token."*
- §10 "Security Architecture Boundary" — Authentication row: *"OIDC/OAuth authorization code + PKCE. **Business API does not implement password verification.**"*
- §21 Architecture Decision Register — **ARCH-05**: *"OIDC/OAuth authorization code + PKCE; **external identity provider**; no custom password auth in business API." Status: **Security Review**.*
- §22 "Deferred Decisions" — *"Exact Identity Provider and tenant onboarding method | Required Before: Security implementation / Production."*

DEC-PS1-004 is the opposite architectural direction: an **internal** identity store (ASP.NET Core Identity) that **does** perform password verification inside the business API, explicitly excluding external IdPs for MVP. **This is not a gap to fill in — it is an existing baseline statement that must be formally amended/superseded before RR-ARCH-001 can be called internally consistent.**

| Baseline area | Current coverage | Gap / conflict |
|---|---|---|
| Requirement | §9 NFR: "authentication mechanism...must be finalized in Architecture/Deployment before Production" — defers, does not conflict | No change strictly required, but may note the mechanism is now decided |
| Business Rules | BR-16: tenant/actor authority derived from "authenticated context" — mechanism-agnostic | No conflict |
| State Transition | No auth content | No conflict |
| Use Case | RR-UI-001 §9 confirms: "Authentication/login mechanism | **Deferred to Architecture/Security**" — no Login/Refresh/Revoke use case exists anywhere | **New UCs required**: Login, Token Refresh, Logout/Revoke |
| Data Dictionary | **No User/Role/RefreshToken entity exists in RR-DD-001 at all** | **New section required**: either document ASP.NET Core Identity's own schema (AspNetUsers/AspNetRoles/etc.) as the identity store, or define extension fields carrying company scope (`tenant_id`) and site scope claims plus a `refresh_token` table |
| Database Design | No Identity tables in RR-DBD-001 | **New section required**: ASP.NET Core Identity default tables + extension tables/claims for company/site scope + refresh-token storage + revocation state |
| API Contract | No auth endpoints in the RR-API-001 catalog | **New endpoints required**: `POST /api/v1/auth/login`, `POST /api/v1/auth/refresh`, `POST /api/v1/auth/revoke` (or equivalent) |
| UI Flow | No Login screen in the UI-001..110 inventory (UI-001 Dashboard assumes an already-authenticated session) | **New screen required**: Login (minimum); token-refresh is a technical/background concern, not necessarily a screen |
| Test Cases | TC-SEC-001/002 cover IDOR/concurrency, not login/token flows | **New TC-AUTH-\* cases required**: login success/failure, refresh, revoke, expired/rotated token handling |
| **Solution Architecture** | ARCH-05 = external IdP, Security Review status | **Documentation amendment required — non-blocking for Sprint 1 implementation**: §4, §10, §21 (ARCH-05), §22 must be amended to record DEC-PS1-004 as the approved MVP decision, explicitly noting external-IdP migration remains possible as a *future* approved change (per the decision's own "unless a future approved change requires it" clause) |

**ARCH-05 status: SUPERSEDED.** As of DEC-PS1-004 (confirmed Round 2), RR-ARCH-001's ARCH-05 ("OIDC/OAuth authorization code + PKCE; external identity provider") is superseded by: **ASP.NET Core Identity + JWT access token + refresh token, no external IdP/OIDC for MVP.** DEC-PS1-004 is **already authoritative for MVP implementation** — Sprint 1 may implement authentication against it directly. The RR-ARCH-001 PDF itself has not yet been amended to reflect this; that amendment is required later for full documentation consistency, but **does not block Sprint 1 coding** (tracked in Section 4 as a WARNING-level documentation item, not a blocker).

This finding does not affect Sprint 1 readiness: **Remaining Blockers: 0; Sprint 1 remains FINAL GO.** RR-ARCH-001's un-amended wording is a documentation-consistency gap to close later, not a contradiction that halts implementation — DEC-PS1-004 already governs.

---

### DEC-PS1-005 — Attachment Malware Scanning

> Sprint 1 must implement an abstraction/interface for malware scanning and enforce secure upload validation. Selection and integration of the production malware scanning provider is deferred to the Security Hardening / Deployment sprint. This must not block Sprint 1 development, but production deployment must not claim malware scanning is active until a real provider is configured and tested.

**Current baseline state — already well aligned; smallest gap of the five decisions.** The baseline already anticipated exactly this shape:

| Baseline area | Current coverage | Gap |
|---|---|---|
| Requirement | §9 NFR: "malware scan; only CLEAN file can satisfy mandatory evidence" | None |
| Business Rules | BR-06: category-required CLEAN evidence | None |
| State Transition | No dedicated file-scan state table exists | Optional — could add a minimal `PENDING → CLEAN / FAILED` transition table for completeness, not required by the decision |
| Use Case | UC-RR-001 "Audit/Notification" column already references "file scan events as applicable" | None |
| Data Dictionary | `FAS-008 malware_scan_status enum PENDING/CLEAN/FAILED` already exists | None |
| Database Design | `CHECK(file_asset.malware_scan_status IN ('PENDING','CLEAN','FAILED'))` already exists | None |
| API Contract | `FILE-API-001` upload already exists | Minor clarification: upload always returns `PENDING` immediately; scanning is out-of-band (already implied by ARCH §11, not yet stated explicitly in RR-API-001 itself) |
| UI Flow | PENDING/FAILED/CLEAN badges already specified (§1, §6) | None |
| Test Cases | `TC-RR-002`, `TC-WO-007` already test PENDING/FAILED/CLEAN enforcement | Optional new case: verify upload succeeds and returns PENDING when no scanning provider is configured (interface stub behavior) |
| Solution Architecture | §11 "File and Evidence Architecture" + §22 Deferred Decisions already list "Blob malware scanning provider | Required Before: Production evidence upload" | **Low-impact wording update**: confirm the deferral as *approved* (DEC-PS1-005) rather than merely *undecided*, and add the explicit Sprint-1 requirement for a pluggable scanning **interface/abstraction** (not currently stated — ARCH describes the scan as "asynchronous" but does not mandate an interface pattern for provider pluggability) |

---

### DEC-PS1-013 — Master Data Deactivation Dependency

> Tenant → Customer → Site → Equipment hierarchy. Customer must belong to Tenant, Site must belong to Customer, Equipment must belong to Site. Customer cannot be deactivated while an active Site exists. Site cannot be deactivated while active Equipment exists. No automatic cascade deactivation.

**Resolves** the Round-1 open modeling question (§DEC-PS1-001, "Customer-vs-tenant_id 1:1"): Customer is now confirmed **not** the same entity as Tenant — Tenant is the security/company boundary (`tenant_id`, unchanged, still server-derived per D-11); Customer is a business master entity scoped *within* a Tenant. Cross-tenant access remains prohibited (existing D-11/BR-16 principle, unchanged).

| Baseline area | Gap |
|---|---|
| Requirement | Scope Boundary + new FR must state the 4-level hierarchy and the deactivation-dependency guard |
| Business Rules | New BR(s): "Deactivate Customer" guarded by "no ACTIVE Site under this Customer"; "Deactivate Site" guarded by "no ACTIVE Equipment under this Site"; explicit **no cascade** — deactivating a parent never auto-deactivates children |
| State Transition | Customer/Site `ACTIVE→INACTIVE` transitions (`ST-CUST-00x`, `ST-SITE-00x`) gain a precondition guard referencing child-entity active count |
| Use Case | Deactivate Customer / Deactivate Site use cases specify the dependency-check step and denial path |
| Data Dictionary | No new fields required beyond `status`; the guard is a query-time check (active-child count), not a stored field |
| Database Design | No schema change beyond FK + status columns already proposed; guard enforced in the Application layer at deactivate-command time (consistent with RR-ARCH-001's "no business rule in Infrastructure/DB" boundary) |
| API Contract | Deactivate endpoints must return `409 STATE_CONFLICT` (existing error-contract code) when the dependency guard fails, not a new error code |
| UI Flow | Deactivate confirmation dialogs must show *why* denial occurred (e.g. "Cannot deactivate — 3 active Sites exist") |
| Test Cases | New cases: deactivate Customer with active Site → denied; deactivate Site with active Equipment → denied; deactivate Customer/Site with zero active children → succeeds; deactivating a Site does **not** auto-deactivate its Equipment (no-cascade proof) |

---

### DEC-PS1-014 — Master Data Deactivation Reason

> Customer, Site, and Equipment all require `deactivate_reason` on Activate→Deactivate.

**Supersedes** the Round-1 framing for Customer/Site (previously "not mandated — open question," see the now-resolved item in DEC-PS1-001/002 field tables above). Equipment's reason requirement (DEC-PS1-003) was already decided via the existing BR-04 reason+audit convention; this decision **extends the same convention to Customer and Site**, so all three master entities are now uniform.

| Baseline area | Gap |
|---|---|
| Business Rules | New BR(s) applying the BR-04 reason/audit pattern to Customer and Site deactivation (previously only applied to Equipment) |
| Data Dictionary | `deactivate_reason` field on all three proposed entity tables changes from optional/nullable to **required when `status` transitions to `INACTIVE`** (same `Y@Deactivate` conditional-required pattern as `EQP-007`) |
| API Contract | Deactivate command body requires `reason` (non-empty) for all three entities, mirroring existing `POST .../reject` shape |
| UI Flow | Deactivate confirmation dialogs require reason input for all three (previously only specified for Equipment) |
| Test Cases | New cases: "deactivate Customer/Site without reason → denied," mirroring the existing Equipment case and the established `TC-RR-007/009` pattern |

---

### DEC-PS1-015 — Master Code Uniqueness

> `customer_code` unique within Tenant. `site_code` unique within Customer. `equipment_code` unique within Site.

**Confirms** the previously-proposed uniqueness scope (Round 1 register flagged this as "PROPOSED, unconfirmed"); it is now decided as stated.

| Baseline area | Gap |
|---|---|
| Business Rules | New BR(s) stating the three uniqueness scopes explicitly |
| Data Dictionary | `customer_code`, `site_code`, `equipment_code` fields marked with their confirmed uniqueness scope |
| Database Design | `UNIQUE(tenant_id, customer_code)`, `UNIQUE(customer_id, site_code)`, `UNIQUE(site_id, equipment_code)` constraints, matching the existing baseline's constraint-naming convention (e.g. `UNIQUE(work_order.repair_request_id)`) |
| API Contract | Create/Edit commands return `422 VALIDATION_FAILED` (existing error code) on a duplicate code within scope |
| Test Cases | New cases: duplicate `customer_code` within same Tenant → denied; same code across different Tenants → allowed; duplicate `site_code` within same Customer → denied; same code across different Customers → allowed; equivalent pair for `equipment_code`/Site |

---

### DEC-PS1-016 — Portfolio Approval Model

> Portfolio Project Owner Approval. External Business Owner: N/A. External Project Manager: N/A. Development Status: Approved for Portfolio Development.

Formal decision record for the approval-model change already applied to this document's Document Control (see "Approval model note" above) and to Section 4, item 6 (reclassified WARNING → INFORMATIONAL). Recorded here as a numbered decision for traceability alongside DEC-PS1-001..015, per the same register convention. No external name, signature, or date is fabricated; this decision does not change any locked business semantics of the underlying baseline (State/Role/Guard/SLA/Permission/Notification), only the project's own governance/sign-off model.

---

## 2. Baseline Sections Requiring Update — Consolidated

| Document | Sections needing new content or wording change | Driven by |
|---|---|---|
| RR-REQ-001 (Requirement & Scope) | Scope Boundary (add Customer/Site/Equipment master management to In Scope); new FR row(s) for master-data CRUD | DEC-PS1-001/002/003 |
| BR-RR-BASELINE | New BR(s) for Customer/Site/Equipment create/edit/activate/deactivate guards and company-scope enforcement; explicit cross-reference of Equipment deactivate-reason to existing BR-04 | DEC-PS1-001/002/003 |
| RR-STS-001 (State Transition) | New state models + transition IDs for Customer, Site, Equipment (`ACTIVE`/`INACTIVE`) | DEC-PS1-001/002/003 |
| UC-RR-001 (Use Case) | New UCs for Customer/Site/Equipment CRUD + activate/deactivate; new UCs for Login/Refresh/Revoke | DEC-PS1-001/002/003, DEC-PS1-004 |
| RR-DD-001 (Data Dictionary) | New entity tables: Customer, Site, Equipment; new Identity-related section (User/Role/RefreshToken or ASP.NET Core Identity schema reference + scope claims) | DEC-PS1-001/002/003, DEC-PS1-004 |
| RR-ERD-001 (ER Diagram) *(not in the requested cross-reference list but structurally required)* | New entities + relationships: Customer 1:N Site 1:N Equipment; Identity/RefreshToken relationship to business `tenant_id`/`site_id` | DEC-PS1-001/002/003, DEC-PS1-004 |
| RR-DBD-001 (Database Design) | New tables + constraints + indexes for Customer/Site/Equipment; new Identity tables/extension tables (ASP.NET Core Identity default schema + refresh-token storage + revocation state) | DEC-PS1-001/002/003, DEC-PS1-004 |
| RR-API-001 (API Contract) | New CRUD + activate/deactivate endpoints for Customer/Site/Equipment; new `/api/v1/auth/*` endpoints; minor clarification that upload always returns PENDING | DEC-PS1-001/002/003, DEC-PS1-004, DEC-PS1-005 (minor) |
| RR-UI-001 (UI Flow) | New Master-Data screens for Customer/Site/Equipment (§9 row changes from "Deferred" to "In Scope"); new Login screen | DEC-PS1-001/002/003, DEC-PS1-004 |
| RR-TC-001 (Test Cases) | New TC-CUST-\*, TC-SITE-\*, TC-EQP-\*, TC-AUTH-\* cases + UAT scenarios | DEC-PS1-001/002/003, DEC-PS1-004 |
| **RR-ARCH-001 (Solution Architecture)** | **§4, §10, §21 (ARCH-05 → SUPERSEDED), §22 — amend authentication decision to ASP.NET Core Identity per DEC-PS1-004 (documentation amendment required, non-blocking for Sprint 1 — see above)**; §11/§22 confirm malware-scan deferral as approved (DEC-PS1-005, low-impact); §15/§26/§30 update PERF-DEC-01 from "Pending Approval" to "Approved: p95 ≤ 3.0s" | DEC-PS1-004 (non-blocking), DEC-PS1-005 (minor), PERF-DEC-01 approval |
| RR-PERF-001 (Performance Test Specification) | §1 "Scope/Non-Goals" and Exit Criteria — PERF-DEC-01 status from "Pending Approval" to "Approved"; workload/test conditions (100 concurrent, 100,000 records/Site, PTP-01 ramp profile) are **unchanged**, not re-derived | PERF-DEC-01 approval |
| BR-RR-BASELINE (additional) | New BR(s) for deactivation-dependency guard (no cascade), uniform deactivate-reason requirement, and confirmed code-uniqueness scopes | DEC-PS1-013/014/015 |
| RR-DD-001 (additional) | Customer/Site/Equipment field tables gain confirmed uniqueness scope and uniform `deactivate_reason` (required) | DEC-PS1-014/015 |
| RR-DBD-001 (additional) | Scoped `UNIQUE` constraints per DEC-PS1-015; deactivation-dependency guard enforced in Application layer, not a DB constraint | DEC-PS1-013/015 |

No section of any document needs to be **removed** or have its locked business semantics **changed** — every update above is additive (new entities/endpoints/screens/tests) or a status/wording amendment (ARCH auth decision, ARCH malware-scan framing, PERF-DEC-01 status).

---

## 3. Performance Decision — PERF-DEC-01 APPROVED (Round 2)

**PERF-DEC-01 is now APPROVED: p95 end-to-end API response time ≤ 3.0 seconds**, under the workload/test conditions already defined in RR-PERF-001 (100 concurrent users, 100,000 Repair Request records per Site, PTP-01 ramp/duration profile: 5-minute ramp-up, 30-minute steady-state, 5-minute ramp-down, plus one warm-up run). **No new workload or performance figures are introduced** — the approval adopts exactly the recommendation and conditions already present in the baseline (RR-REQ-001 §17, RR-PERF-001 §1, RR-ARCH-001 §26, etc.); only the previously-unresolved statistic choice (Average/Max/p95/p99) is now decided, as p95.

This closes the item that was carried forward unchanged from Round 1. The 8 documents that restate PERF-DEC-01 (RR-REQ-001, UC-RR-001, RR-TC-001, RR-API-001, RR-UI-001, RR-PERF-001, RR-ARCH-001, RR-REV-001) still literally say "Pending Approval" in their PDF text — that wording is **superseded** by this decision and needs a status-only wording update (tracked in Section 4), not a content or workload change.

---

## 4. Remaining Items After These Decisions (Round 2)

**Remaining Blockers: 0.** This register is now the authoritative Pre-Sprint-1 decision register and baseline-amendment reference (see Purpose, above). Items previously classified as BLOCKER solely because the decided content was "not yet authored into the PDFs" are reclassified to WARNING: the decisions themselves (DEC-PS1-001..016) are final and authoritative here, so PDF wording amendment is follow-up documentation work, not a precondition for Sprint 1 coding.

| # | Item | Classification | Status change |
|---|---|---|---|
| 1 | Customer/Site/Equipment field-level baseline (DEC-PS1-001/002/003/013/014/015) not yet authored into the PDFs | **WARNING** — documentation lag only; decisions are final in this register | Reclassified this round: BLOCKER → WARNING |
| 2 | Customer-vs-tenant_id modeling question | **RESOLVED** | Closed by DEC-PS1-013: Tenant is the security boundary; Customer is a distinct business entity scoped under Tenant; full `Tenant → Customer → Site → Equipment` hierarchy confirmed |
| 3 | RR-ARCH-001 ARCH-05 wording not yet amended | **WARNING** — documentation lag only; ARCH-05 is SUPERSEDED by DEC-PS1-004 as of this register | Reclassified this round: BLOCKER → WARNING |
| 4 | Auth entities/endpoints/screens/tests not yet authored into the PDFs | **WARNING** — documentation lag only; DEC-PS1-004 already fixes the mechanism and shape | Reclassified this round: BLOCKER → WARNING |
| 5 | Deactivation-dependency guard, uniform deactivate-reason, and code-uniqueness constraints not yet authored into BR/DD/DBD/API/UI/TC | **WARNING** — documentation lag only; DEC-PS1-013/014/015 are final | Reclassified this round: BLOCKER → WARNING |
| 6 | Malware-scan provider not selected | **WARNING** — does not block Sprint 1 (interface-only), blocks production claim of active scanning | Unchanged |
| 7 | No external Business Owner/PM — Portfolio Project Owner Approval model | **INFORMATIONAL** | Unchanged; recorded as DEC-PS1-016, Approved |
| 8 | Stale sibling-version citations across baseline documents (prior audit Matrices C/D) | **WARNING** | Unchanged |
| 9 | PERF-DEC-01 statistic | **RESOLVED — APPROVED** (p95 ≤ 3.0s) | Closed this round (Section 3); PDF wording ("Pending Approval") still needs a status-only update — tracked as documentation lag (item 8-style WARNING), not an open decision |
| 10 | Exact CORS origins / rate-limit thresholds | **INFORMATIONAL** | Unchanged |
| 11 | Backup/RTO/RPO, alert thresholds, on-call procedure | **INFORMATIONAL** | Unchanged |
| 12 | Exact report/export format & max page size | **INFORMATIONAL** | Unchanged |
| 13 | Whether Customer/Site deactivation guard should also check for *open/active transactional records* (e.g. in-progress Repair Requests under a Site), beyond just active child master-data rows | **OPEN — not decided; does not block Sprint 1 start** | DEC-PS1-013 explicitly guards on active **Site**/active **Equipment** counts only; it does not address in-flight Repair Requests/Work Orders. Not assumed here — flagged for a future decision if needed. **v1.2:** the Sprint 1 implementation state is recorded as DEC-S1-004-D4 (§6.1): no open-Repair-Request guard is implemented, and this item remains OPEN/DEFERRED |

---

## 5. Sprint 1 FINAL GO / NO-GO Recommendation

**Sprint 1: FINAL GO. Remaining Blockers: 0.**

All modeling ambiguity is now closed: hierarchy (DEC-PS1-013), deactivation guards and no-cascade rule (DEC-PS1-013), uniform deactivate-reason (DEC-PS1-014), code uniqueness (DEC-PS1-015), authentication architecture (DEC-PS1-004 — ASP.NET Core Identity + JWT + refresh token; external IdP/OIDC SUPERSEDED for MVP), Portfolio Project Owner approval (DEC-PS1-016 — Approved for Portfolio Development), and PERF-DEC-01 (Approved: p95 ≤ 3.0s, Section 3) are all decided. What remains is **authoring the decided content into the PDF baseline documents** (Section 2) as follow-up documentation work — with one narrow, non-blocking open question (Section 4, item 13: transactional-record deactivation guard).

Recommended sequencing (unchanged from Round 1, now with fewer open questions):

- **Start immediately, no blocker:** Repair Request, Audit Log, Attachment upload/scan-interface scaffolding.
- **Start immediately, technical scaffolding only:** ASP.NET Core Identity/JWT/refresh-token wiring, DbContext/migrations.
- **Author decided baseline sections, then code:** Customer/Site/Equipment CRUD + Activate/Deactivate business logic, using the field-level proposal, guards, and uniqueness rules already fixed by DEC-PS1-013/014/015 in this register — no further clarification needed before authoring.
- **Required before claiming architecture compliance (not before coding):** amend RR-ARCH-001 (ARCH-05, PERF-DEC-01 status) to match the now-approved decisions.

No blocker in this register is a reason to halt Sprint 1.

---

## 6. Sprint 1 Implementation Decisions (v1.2)

**v1.2 revision rule:** Sections 6–10 record decisions the Portfolio Project Owner already approved during S1-004, S1-005, S1-006 and the Pre-S1-007 requirement-resolution session (14 September 2026).
- They are written down here for traceability. This revision creates no new business rule.
- No PDF baseline document is modified. The implementation evidence is the committed code and tests on `feature/s1-006-attachment-upload` (c03c4c4, 46f12a6, ce69f6e).
- Original ticket-local IDs are kept inside the register IDs: `DEC-S1-004-D1` is S1-004 decision D1.

**Status vocabulary:**

| Status | Meaning |
|---|---|
| APPROVED – IMPLEMENTED | Approved and present in committed code/tests |
| APPROVED – NOT YET IMPLEMENTED | Approved; implementation belongs to the named ticket |
| SUPERSEDED | Replaced by a later decision; kept for history, never deleted |
| DEFERRED | Explicitly out of the current scope; not an approved requirement |
| OPEN | Not decided |

### 6.1 S1-004 — Customer / Site / Equipment Master (commit c03c4c4)

#### DEC-S1-004-D1 — Reactivation clears the deactivate reason
> A successful reactivation (INACTIVE → ACTIVE) clears the stored `deactivate_reason`. The historical reason remains in audit history.

- **Status:** APPROVED – IMPLEMENTED
- **Applies to:** Customer, Site, Equipment
- **Evidence:** `MasterDataEntity.Activate()` ("The stored deactivate reason is cleared (S1-004 decision D1); the previous reason remains in the deactivation audit record"). The activation audit also captures the previous reason.
- **Tests:** CustomerEndpointsTests, CustomerServiceTests.
- **Relation:** Extends DEC-PS1-014 (reason required on deactivate) and the BR-04 reason/audit convention. It does not change either.

#### DEC-S1-004-D2 — Activation requires an active parent
> A Site cannot be activated while its Customer is inactive. Equipment cannot be activated while its Site is inactive.

- **Status:** APPROVED – IMPLEMENTED
- **Behaviour:** Checked in the same SERIALIZABLE transaction as the change. Failure → `422 VALIDATION_FAILED` on the parent field. Already-active → `409 STATE_CONFLICT`.
- **Evidence:** `MasterDataCommandSteps.ActivateAsync`, `SiteService.ActivateAsync`, `EquipmentService.ActivateAsync`.
- **Tests:** SiteAndEquipmentServiceTests, SiteAndEquipmentEndpointsTests.
- **Relation:** Mirrors the active-parent guard on create (DEC-PS1-002/003) for the Activate transition. The DEC-PS1-013 no-cascade rule is unchanged: activating a parent never activates children.

#### DEC-S1-004-D3 — Update never re-parents
> A normal Update does not move a Site to another Customer or Equipment to another Site. The code is the only editable field (Customer, Site, Equipment).

- **Status:** APPROVED – IMPLEMENTED
- **Behaviour:** PATCH bodies carry only the code. Parent ids in the body are ignored. An unchanged code writes and audits nothing.
- **Evidence:** `Site.cs` / `Equipment.cs` ("owning Customer/Site never changes after creation (S1-004 decision D3)"); `MasterDataEntity.ValidCode` ("code is the only editable field").
- **Tests:** SiteAndEquipmentServiceTests, CustomerServiceTests.
- **Relation:** Consistent with the DEC-PS1-013 hierarchy and DEC-PS1-015 code uniqueness within the parent.

#### DEC-S1-004-D4 — No open-Repair-Request deactivation guard
> No open-Repair-Request deactivation guard is implemented. The broader transactional-record decision remains deferred/open.

- **Status:** APPROVED – IMPLEMENTED (absence of guard). The underlying question stays **OPEN / DEFERRED** (Section 4, item 13).
- **Behaviour:** Customer/Site deactivation guards count only active child master rows (DEC-PS1-013). No Repair Request or Work Order query is made.
- **Evidence:** `MasterDataCommandSteps.DeactivateAsync` and the `CountActive*` guards. No code comment carries the label "D4"; the evidence is the guard implementation.
- **Relation:** Does not answer Section 4 item 13. A future transactional-record guard needs a separate decision.

### 6.2 S1-005 — Repair Request Draft Create / Edit (commit 46f12a6)

#### DEC-S1-005-E1 — Category, Priority, Location and Contact not accepted in the Draft (original)
> Category, priority, location and contact are not accepted/editable until their masters are defined.

- **Status:** **SUPERSEDED** (v1.2)
  - For **Category and Priority** by DEC-PRE-S1-007-01/-02.
  - For **Contact** by DEC-PRE-S1-007-04.
  - For **Location** by DEC-PRE-S1-007-05, which keeps `locationId` not accepted, now as an explicit Sprint 1 deferral rather than "until the master exists".
- **Implementation state:** The S1-006 code (ce69f6e) implemented E1: `RepairRequestDraftFields`, `RepairRequestDraftRequest`, `RepairRequest.EditDraft`. The replacing decisions are APPROVED – IMPLEMENTED in S1-007 (commit 36ffc6a).
- **Evidence:** Code comments "S1-005 decision E1".
- **History:** Original text kept verbatim above. Not deleted.

#### DEC-S1-005-E2 — Whole-Draft revalidation on every save
> The complete resulting Draft is validated on every save.

- **Status:** APPROVED – IMPLEMENTED
- **Behaviour:** Create and PATCH run the same validation over the resulting Draft: Site in business scope and ACTIVE, Equipment ACTIVE and belonging to the Site, description ≤2000, preferred end ≥ start. Submit-only requirements (Y@Submit) are not applied while DRAFT.
- **Related contract behaviour:** The PATCH body carries the complete S1-005 editable field set; an omitted field is saved as empty (`RepairRequestDraftRequest`).
- **Evidence:** `RepairRequestDraftService.UpdateAsync` ("Decision E2").
- **Tests:** RepairRequestDraftServiceTests, RepairRequestDraftEndpointsTests.

#### DEC-S1-005-E3 — Detail follows the approved Repair Request scope
> Repair Request detail is returned within the caller's approved Repair Request scope (S1-003). A site-wide role may view the Draft but is never its owner.

- **Status:** APPROVED – IMPLEMENTED
- **Behaviour:**
  - RR-API-004 uses `IDataScope.RepairRequests` (S1-003).
  - Only the owner (`created_by`) may edit (RR-API-002) or upload attachments (FILE-API-001).
  - Out-of-scope and nonexistent ids both return 404.
- **Evidence:** `IRepairRequestDraftStore.GetAsync` ("S1-005 decision E3").
- **Tests:** RepairRequestDraftStoreTests ("Decision E3: a site-wide role may view the Draft but is never its owner"), RepairRequestDraftEndpointsTests.

### 6.3 S1-006 — Secure Attachment Upload (commit ce69f6e)

#### DEC-S1-006-F1 — No business attachment-count limit
> There is no business attachment-count limit.

- **Status:** APPROVED – IMPLEMENTED
- **Behaviour:** The metadata list is bounded only by technical paging (DEC-S1-006-PG). Per-file rules stay PDF/JPG/JPEG/PNG and ≤10 MB (RR-REQ-001 §9).
- **Evidence:** `RepairRequestAttachmentsController` ("there is no business attachment-count limit (decision F1)").

#### DEC-S1-006-F2 — Scanner unavailable or error leaves the file PENDING
> If the scanner is unavailable or errors, the attachment stays PENDING and unusable.

- **Status:** APPROVED – IMPLEMENTED
- **Behaviour:**
  - Clean → CLEAN; Infected → FAILED. Each result is persisted and audited only while the file is still PENDING (`FILE_ASSET_SCAN_CLEAN` / `FILE_ASSET_SCAN_FAILED`).
  - Unavailable or failing scanner/storage → still PENDING, retry later.
  - The default `NotConfiguredMalwareScanner` always reports Unavailable and never claims protection (DEC-PS1-005).
  - PENDING/FAILED files are never served (download → 409).
- **Evidence:** `FileScanService`, `FileScanResult.StillPending` ("decision F2").
- **Tests:** FileScanServiceTests.
- **Note:** No background scan runner exists; see DEFERRED items in Section 8.
- **Clarification (Portfolio Project Owner, 14 September 2026, S1-007 pre-commit review):**
  - F2's "no runner" applies to the S1-006 production/background scanning scope.
  - The opt-in Development/Testing-only fake scanner and PENDING scan runner of DEC-PRE-S1-007-08 are test and development infrastructure for exercising the CLEAN-dependent Submit flow.
  - They do not satisfy or replace the deferred production background Worker.
  - They never change Production, which keeps `NotConfiguredMalwareScanner`; files there stay PENDING and unusable.

#### DEC-S1-006-F3 — Attachment removal not supported
> Attachment removal is not supported in S1-006.

- **Status:** APPROVED – IMPLEMENTED (no removal capability). Removal as a feature is **DEFERRED** (Section 8).
- **Behaviour:** No DELETE endpoint exists. `IFileStorage.DeleteAsync` is used only to compensate a failed upload (a binary must not outlive failed metadata). It is never a business operation.
- **Evidence:** Route catalog of `RepairRequestAttachmentsController` and `FilesController`. No code comment carries the label "F3".

#### DEC-S1-006-F4 — access_scope_code is a policy identifier, not a grant
> `access_scope_code = REPAIR_REQUEST` is a server-controlled policy identifier, not an authorization grant.

- **Status:** APPROVED – IMPLEMENTED
- **Behaviour:**
  - The value is set by the server (RR-DD-001 ATT-014 "Role/site policy key"). It is never client input.
  - Attachment access derives entirely from the Repair Request's own scope:
    - list → RepairRequest.Read scope;
    - upload → owner + DRAFT;
    - download → Repair Request scope + CLEAN.
- **Evidence:** `AttachmentFileRules.AccessScopeCode` ("Decision F4: attachment access derives entirely from the Repair Request's own scope").

#### DEC-S1-006-SK — Server-generated opaque storage key
> Storage keys are opaque and generated by the server, never derived from client input. The client filename is sanitized display metadata only and never influences storage. The storage reference is never returned by any API.

- **Status:** APPROVED – IMPLEMENTED
- **Behaviour:** Key = tenant partition + 128-bit random identifier (`AttachmentFileRules.NewStorageKey`). Storage never overwrites. Public URLs are prohibited (RR-DD-001 FAS-007; RR-ARCH-001 §11).
- **Evidence:** `AttachmentFileRules`, `IFileStorage`.
- **ID note:** No letter-number ID was assigned during S1-006. `SK` is a register label only.

#### DEC-S1-006-PG — Bounded, paged attachment metadata listing
> Attachment metadata is listed through a bounded, paged endpoint: `GET /api/v1/repair-requests/{id}/attachments`.

- **Status:** APPROVED – IMPLEMENTED
- **Behaviour:**
  - `page`/`pageSize` follow the shared paging conventions (default/max from configuration; invalid → 400).
  - Scope check, count and one SQL page: three bounded commands, whatever the attachment count.
  - Upload order. Metadata only, including scan status. Out of scope → 404.
- **Evidence:** `RepairRequestAttachmentsController.List`, `AttachmentStore.ListAsync`.
- **API:** RR-API-001-ADD FILE-API-003.
- **ID note:** `PG` is a register label only.

---

## 7. Pre-S1-007 Requirement-Resolution Decisions (v1.2)

**Source:** Pre-S1-007 requirement-resolution session, 14 September 2026, Portfolio Project Owner answers.

- **Status of every decision below:** APPROVED – IMPLEMENTED in S1-007 (commit 36ffc6a), except the portions explicitly DEFERRED in Section 8 (for example the ACTIVE request-contact check, REQ-FU-USR-001).
- **Session item IDs** are prefixed `RES-`, e.g. `RES-M3`. This avoids confusion with the unrelated S1-002 code labels "decision M1/M2/M3/M6/B4".
- **[BASE]** marks content already in the approved baseline. Everything else is the recorded portfolio decision.

#### DEC-PRE-S1-007-01 — Category is tenant-scoped seeded lookup data (RES-B1)
> Category is stored as tenant-scoped lookup data (tenant_id, code, name, ACTIVE/INACTIVE), seeded. Management UI is deferred. The Draft accepts `requestCategoryCode`; Submit requires an ACTIVE code of the same tenant.

- [BASE] `request_category_code` varchar(30), Y@Submit, "Active Category" (RR-DD-001 RR-008).
- [BASE] One category per request; editable only while DRAFT (ST-RR-001).
- [BASE] Master deactivation blocks future selection but keeps history (RR-DBD-001 §6).
- [BASE] Drives approval routing (ARC-003). No SLA effect (BR-12).
- Supersedes DEC-S1-005-E1 for Category.

#### DEC-PRE-S1-007-02 — Priority is tenant-scoped seeded lookup data (RES-B1)
> Priority is stored as tenant-scoped lookup data (code ≤20, name, ACTIVE/INACTIVE), seeded. Management UI is deferred. The Draft accepts `priorityCode`; Submit requires an ACTIVE code of the same tenant.

- [BASE] Requester-selected (FR-01), Y@Submit, "Active Priority" (RR-DD-001 RR-010).
- [BASE] Priority has no SLA effect under BR-12: one Resolution SLA policy covers all requests. No separate "SLA priority" concept exists or is created.
- SLA values and calculation are outside S1-007 (DEC-PRE-S1-007-12).
- Supersedes DEC-S1-005-E1 for Priority.

#### DEC-PRE-S1-007-03 — Portfolio placeholder seed values (RES-M8)
> Placeholder seed values:
> - Priority: LOW, MEDIUM, HIGH, URGENT
> - Category: ELECTRICAL, MECHANICAL, PLUMBING, HVAC, IT, OTHER
>
> These are **demo/reference data only**, not an approved business catalog, and can be replaced as data without a code change.

#### DEC-PRE-S1-007-04 — request_contact_id references an active user in the selected-Site scope (RES-B2, RES-M9)
> `request_contact_id` references an active user of the same tenant, of any role (the Requester included), who has site-wide or selected-Site scope (S1-003).

- It is an FK reference, not a snapshot.
- No Contact master is created.
- No contact name, phone or email fields are added to the Repair Request.
- The contact may differ from the Requester.
- [BASE] Y@Submit, "Active contact in selected Site" (RR-DD-001 RR-018).
- Supersedes DEC-S1-005-E1 for Contact.
- **S1-007 implementation decisions** (approved by the Portfolio Project Owner in the S1-007 pre-implementation session, 14 September 2026):
  - **Enforced** at Draft save and at Submit:
    - the user exists and belongs to the same tenant;
    - the user holds at least one non-ADMINISTRATOR role;
    - the user is assigned (user_site_scope) to the selected Site — S1-003 business Site scope.
  - ADMINISTRATOR-only users are not eligible.
- **Formal deferral of the ACTIVE portion** (Portfolio Project Owner, 15 September 2026, S1-007 pre-commit review):
  > RR-018 (request contact) ACTIVE-user validation is temporarily deferred because the current User model has no authoritative business lifecycle/status. Existence + tenant + Site-scope validation remains mandatory.
  - Only the ACTIVE/inactive user-state portion is deferred. S1-007 still **enforces**, at Draft save and again at Submit:
    - `request_contact_id` references an existing user;
    - the user belongs to the same tenant;
    - the user has business Site scope on the selected Site (a non-ADMINISTRATOR role and a user_site_scope assignment);
    - a cross-tenant contact cannot be selected;
    - an out-of-scope contact cannot be selected;
    - a client-provided contact id never bypasses backend validation (Submit re-validates the stored contact).
  - ASP.NET Core Identity lockout is temporary brute-force protection. It is **not** business active/inactive status and is not used for this rule.
  - No IsActive/UserStatus column or table is added in S1-007.
  - The ACTIVE check must not be described as enforced until REQ-FU-USR-001 is delivered (Section 8.1; Section 9 NB-10).
  - **ID note:** the Project Owner's instruction referred to "CAC-005". In RR-DD-001, CAC-005 is `customer_acceptance_contact.user_id` (Work Order acceptance contact; with CAC-009 `is_active` "Must be active at acceptance"). The rule deferred here is RR-018. CAC-005/CAC-009 are unchanged and outside S1-007 scope.

#### DEC-PRE-S1-007-05 — Location is not in Sprint 1 (RES-M1)
> Location is not in Sprint 1:
> - `locationId` is not accepted; `location_id` stays null.
> - No Location table is created.
> - The category-conditional Equipment/Location rule is deferred; no category makes Equipment or Location mandatory in Sprint 1.
> - Equipment stays optional (if present: ACTIVE and belongs to the Site).

- [BASE] Location is a sub-location that belongs to a Site (RR-DD-001 RR-007). This decision does not remove it from the baseline; it defers it.
- Replaces DEC-S1-005-E1's "until masters exist" rationale for Location.

#### DEC-PRE-S1-007-06 — At least one CLEAN image is required at Submit (RES-M2, RES-M7)
> Submit requires at least one attachment whose validated type is JPG/JPEG/PNG (`image/jpeg` or `image/png`) **and** whose `malware_scan_status` is `CLEAN`. A PDF does not count.
>
> Submit validation counts **only** CLEAN JPG/JPEG/PNG attachments.
>
> - At least one CLEAN JPG/JPEG/PNG attachment is required.
> - CLEAN JPG/JPEG/PNG satisfies mandatory photo evidence.
> - PENDING attachments do not satisfy mandatory evidence.
> - FAILED attachments do not satisfy mandatory evidence.
>
> If no qualifying CLEAN image exists, Submit fails:
> - the approved `422 VALIDATION_FAILED` response is returned;
> - state stays DRAFT;
> - `request_no` stays null (no Request No is generated);
> - the SLA does not start (no SLA start marker is recorded; DEC-PRE-S1-007-12);
> - no Submit transition and no Submit success audit occurs.

- This is a **portfolio rule added beyond the baseline**. The baseline has no attachment minimum at Submit.
- Requiring CLEAN follows from [BASE] RR-REQ-001 §9: "only CLEAN file can satisfy mandatory evidence".

#### DEC-PRE-S1-007-07 — PENDING and FAILED attachments do not block Submit — FINAL S1-007-START DECISION (RES-M2; approved by the Portfolio Project Owner, 14 September 2026)
> An additional FAILED attachment does NOT block Repair Request Submit.
>
> - PENDING attachments do not block Submit.
> - FAILED attachments do not block Submit.
> - FAILED attachments remain unusable and must not be downloadable or treated as evidence.
> - Only CLEAN files are downloadable or usable.
>
> **Reason:** S1-006 does not support attachment removal yet. Making FAILED attachments block Submit could permanently trap an otherwise valid Draft after the user has uploaded a replacement CLEAN image.
>
> **Security remains fail-closed for the failed file itself:**
> - it cannot be consumed;
> - it cannot be downloaded;
> - it cannot satisfy evidence;
> - it cannot participate in downstream file processing.

- **Consistent with implemented S1-006 behaviour:**
  - FILE-API-002 serves only CLEAN files; PENDING and FAILED return `409 STATE_CONFLICT` (`RepairRequestAttachmentService.DownloadAsync`; DEC-S1-006-F2).
  - The scan service processes only PENDING files (`AttachmentStore.FindPendingFileAsync`).
  - Attachment removal remains not supported (DEC-S1-006-F3).
- **History:** The earlier v1.2 wording (commit a10cab8) recorded "whether an additional FAILED attachment blocks Submit" as **MUST RESOLVE AT S1-007 START** (NB-5). The Portfolio Project Owner has now decided it: FAILED attachments do not block Submit.

#### DEC-PRE-S1-007-08 — Dev/Test-only fake malware scanner (RES-M6)
> A Development/Test-only fake scanner that marks files CLEAN, plus an in-process scan runner, may be added as **test infrastructure only**.

- Application startup must refuse it outside Development/Test.
- Production keeps `NotConfiguredMalwareScanner`.
- It must never be described as malware protection. DEC-PS1-005 is unchanged.
- Production scanning provider and background scan Worker remain DEFERRED.
- **Status: APPROVED – IMPLEMENTED in S1-007 (commit 36ffc6a)**, as decided by the Portfolio Project Owner during the S1-007 pre-commit review (14 September 2026). It replaces the earlier S1-007 pre-implementation choice to exclude it, which was never recorded here.
  - **Opt-in:**
    - setting `DevelopmentMalwareScanning:Enabled` (default `false`);
    - the committed `appsettings.Development.json.example` enables it for local Development;
    - the Testing environment enables it only in tests that ask for it.
  - **Allowed environments:** Development and Testing only.
    - With it enabled in any other environment, including Production, the API refuses to start.
    - The check runs at registration and again as start-time options validation.
  - **Behaviour** (deterministic test infrastructure; no malware detection, no malware protection):
    - `DevelopmentOnlyFakeMalwareScanner` reports content containing the ASCII marker `RR-DEV-FAKE-SCAN-FAILED` as infected (file becomes FAILED), and everything else as clean (file becomes CLEAN).
    - `DevelopmentOnlyPendingScanRunner` polls PENDING files in bounded batches and calls the existing `FileScanService`, so S1-006 transition, audit and "only while PENDING" rules are unchanged.
  - **Production:** nothing is registered.
    - `NotConfiguredMalwareScanner` stays the scanner.
    - Files stay PENDING and unusable (fail-closed, unchanged).
  - **Relation to DEC-S1-006-F2:** F2's "no runner" applies to the S1-006 production/background scanning scope. This runner is development/test infrastructure only and does not satisfy or replace the deferred production Worker.

#### DEC-PRE-S1-007-09 — Duplicate matching when Equipment/Location is empty (RES-H1)
> BR-14 duplicate matching uses exact key equality on tenant + Site + Category + Equipment:
> - Empty Equipment matches only active requests at the same Site + Category that also have no Equipment.
> - With Equipment, only the same Equipment matches.
> - Location is always empty in Sprint 1 (DEC-PRE-S1-007-05).
> - No fuzzy matching.

- [BASE] BR-14 / D-12 active set: SUBMITTED, UNDER_REVIEW, APPROVED, and CONVERTED while its Work Order is not CLOSED/CANCELLED. DRAFT/REJECTED/CANCELLED are excluded.
- [BASE] Within 24h. Warning, not hard block. A continuation reason is required to proceed, persisted in RR-011 and audited.
- **Implementation — concurrency** (S1-007 pre-commit review, 15 September 2026; implementation only, the rule above is unchanged):
  - The duplicate count, the continuation-reason decision, Request No allocation, the DRAFT → SUBMITTED transition and the audit run in **one** Submit transaction.
  - That transaction first takes a transaction-owned SQL Server application lock (`sp_getapplock`, Exclusive) on the duplicate key: tenant + Site + Category + Equipment, with "no Equipment" as its own key.
  - Two concurrent Submits of different matching Drafts are therefore serialized. The later one counts the committed earlier one and, without a continuation reason, receives the duplicate warning. It stays DRAFT, with no Request No, no SLA start and no audit.
  - The lock is held by SQL Server, so it is correct across API instances. It is released on commit, rollback or connection loss.
  - Submits with a different duplicate key do not wait on it. Lock order is always duplicate key → Request No counter row → repair_request row.
  - Lock wait timeout (10 s) or deadlock → 409 CONCURRENCY_CONFLICT with nothing written. There is no automatic retry.
  - When Location enters the duplicate match (DEC-PRE-S1-007-05), it must be added to the lock key.

#### DEC-PRE-S1-007-10 — Duplicate Submit response (RES-M4)
> A BR-14 match submitted without a continuation reason returns `422 VALIDATION_FAILED` with an error on `duplicateContinuationReason` and a `duplicateCount`. No identifiers of other requests are returned. The request stays DRAFT.

#### DEC-PRE-S1-007-11 — Request No format and scope (RES-M3)
> Request No has the format `RR-{yyyy}-{000000}`, using the UTC year of Submit, with one counter per tenant per year.
> - The counter is incremented inside the Submit transaction, so a failed or rolled-back Submit uses no number (no gaps).
> - Uniqueness is `(tenant_id, request_no)`, replacing the current global filtered unique index.

- [BASE] Generated only at SUBMITTED; unique; immutable; varchar(30); never client input (BR-01, RR-DD-001 RR-003).

#### DEC-PRE-S1-007-12 — Submit and SLA-start scope (RES-M5; revised by the Portfolio Project Owner, 14 September 2026)
> S1-007 Submit is one atomic transaction covering:
> 1. DRAFT → SUBMITTED with `submitted_by` / `submitted_at`.
> 2. Duplicate check with continuation reason.
> 3. Request No.
> 4. Audit.
>
> Approved S1-007 scope: **Submit + SLA start.** A successful Submit records the SLA start timestamp (start marker).
>
> This is not approval of an SLA calculation engine. S1-007 Submit does **not**:
> - calculate or store an SLA due time;
> - calculate or store a risk threshold;
> - evaluate breach;
> - escalate;
> - route (ST-RR-003) or notify (NTF-SUBMITTED);
> - apply an SLA calendar or working hours.
>
> These remain deferred to the approved SLA Policy / Monitoring scope.
>
> Until routing exists the request stays SUBMITTED, which the baseline allows ("routing failure stays SUBMITTED").

- **SLA start marker in S1-007:** `repair_request.submitted_at` (RR-DD-001 RR-014, "SLA start"; RR-013/014 derived on Submit), set in the Submit transaction.
  - No `sla_record` row is written in S1-007: SLA-004 `sla_policy_version` and SLA-006 `original_due_at` are required (Y) fields of that table and belong to the deferred SLA Policy / Monitoring scope.
- **Baseline SLA values (not introduced by this session):**
  - The approved baseline already defines the Resolution SLA duration and risk threshold, independently of this requirement resolution:
    - BR-12 / FR-08 "Resolution SLA = 24h 24/7 from SUBMITTED"; UC-SLA-001 "MVP policy 24h 24/7"; EV-SLA-001; RR-DD-001 SLA-006.
    - D-13 / BR-17 "risk at effective_due_at − 4 hours"; EV-SLA-001/002; RR-DD-001 SLA-008.
  - These baseline rules are retained unchanged. This decision neither invents nor alters them, and S1-007 does not calculate, persist or lock them. They are applied in the SLA Policy / Monitoring scope.
- **Revision history:**
  - The first session answer (RES-M5) described Submit as also creating `sla_record` with calculated due and risk values.
  - That calculation scope was **withdrawn** by the Portfolio Project Owner before any implementation.
  - The approved scope is Submit + SLA start marker only, as stated above.
- **Duplicate window unaffected:** The BR-14 24-hour duplicate window (DEC-PRE-S1-007-09) is a duplicate-detection rule, not an SLA value.

---

## 7R. Pre-S1-008 Routing Prerequisite — S1-007R Decisions (v1.2 amendment, 15 September 2026)

**Source:** S1-008 and S1-007R pre-implementation reviews, Portfolio Project Owner answers, 15 September 2026.

- **Why S1-007R exists:**
  - The baseline lets only the **assigned/authorized Approver** Approve or Reject: UC-RR-003 "Role + route + site scope"; RR-REQ-001 §13 "Assigned/authorized Approver".
  - Assignment only comes from System routing: ST-RR-003; RR-DD-001 ARC-* and APR-006 assigned_approver_id.
  - That routing was DEFERRED (DEC-PRE-S1-007-12; Section 8).
  - The Portfolio Project Owner decided **not** to weaken the rule to "APPROVER + Site". The routing prerequisite ships first as S1-007R, and S1-008 stays blocked until then.
- **Status of DEC-PRE-S1-007R-01..10:** APPROVED – IMPLEMENTED in S1-007R (commit b1cad73c000206ba6531e03387245d924a90b747).
- **[BASE] facts used:**
  - ST-RR-003: SUBMITTED → UNDER_REVIEW, actor System, guard "active approval route / approver", failure stays SUBMITTED.
  - RR-DD-001 ARC-001..008 and APR-001..011.
  - RR-DBD-001: approval_route + approval_route_step with UNIQUE(route, step); repair_request_approval with UNIQUE(request, step); approval inbox index.
  - ERD-DR-02.
  - TC-RR-005 / UAT-03: no approver → remains SUBMITTED, no auto approve.
  - RR-UI-001: "authorized staff sees routing issue".

#### DEC-PRE-S1-007R-01 — Routing runs right after the Submit commit
> Routing (ST-RR-003, actor System) runs in the same HTTP request, immediately after the Submit transaction commits, in its own transaction. Success → UNDER_REVIEW. Failure → the request stays SUBMITTED.

- A routing failure or unexpected routing error never rolls back or fails the committed Submit. It is logged with the correlation id only; recovery is DEC-PRE-S1-007R-07.
- **Contract change to S1-007 RR-API-005:** the Submit response reflects the final state, `status` = SUBMITTED or UNDER_REVIEW, with the matching ETag.
- **Post-commit response rule (S1-007R pre-commit review, Portfolio Project Owner, 15 September 2026):** once Submit has committed, a routing or final-state read-back failure never turns the response into a failure. The response is the committed state (SUBMITTED, Request No, submitted_at, committed ETag). UNDER_REVIEW is returned only after a successful routing result, and Submit is never re-run.
- **Audit actor (same review):** routing audit records carry the System actor (ST-RR-003). The initiating user (requester on Submit, ADMINISTRATOR on retry) is kept only as `initiatedBy` in the audit value document, with the trigger and correlation id. No separate audit mechanism is added.

#### DEC-PRE-S1-007R-02 — Exactly one assigned approver
> The step's `approver_user_id` is assigned when set and eligible. Otherwise exactly one eligible user is assigned. Zero or several eligible users is a routing failure; no selection algorithm is invented.

#### DEC-PRE-S1-007R-03 — Single-step routes only (MVP)
> A route is created with exactly one step (step_no 1). A route that does not resolve to exactly one valid step is a routing failure (ROUTE_STEP_INVALID). Sequential multi-step approval is DEFERRED.

#### DEC-PRE-S1-007R-04 — Route configuration by Administrator API
> Tenant-scoped ADMINISTRATOR endpoints (MasterData.Manage) create, list, get, activate and deactivate routes with their single step. No UI.

- There is no update endpoint: change a route by deactivating it and creating a new one.
- ARC-008 is a flag only, so no deactivation reason is required.
- These are addendum endpoints outside the RR-API-001 catalog.
- Configuration never grants Approve or Reject.

#### DEC-PRE-S1-007R-05 — Approver eligibility
> The assigned approver must:
> - hold **APPROVER**;
> - have **business Site scope** (user_site_scope) on the request's Site;
> - belong to the same tenant;
> - **not** be the request's created_by.

- `approver_role_code` may only be APPROVER: rejected at configuration and enforced by CHECK.
- ADMINISTRATOR-only users never qualify.
- The "active approver" user-state check is **DEFERRED to REQ-FU-USR-001**. Identity lockout is not business status.

#### DEC-PRE-S1-007R-06 — One active route per key
> - At most one ACTIVE route per tenant + Category + Site.
> - At most one ACTIVE tenant-default route (site null) per tenant + Category.
> - Inactive history may coexist.
> - Selection: exact active Site route → active tenant-default route → routing failure.

- Enforced by application validation **and** by the filtered unique indexes `UQ_approval_route_active_site` / `UQ_approval_route_active_default`.
- More than one active route at the chosen level is ROUTE_AMBIGUOUS; the code never picks the first or lowest id.
- **Persistence note (S1-007R pre-commit review, Portfolio Project Owner, 15 September 2026):**
  - approval_route_step primary key = (tenant_id, approval_route_id, step_no). RR-DBD-001 UNIQUE(route, step) is preserved, because the tenant-composite route foreign key pins tenant_id to the route's tenant.
  - The repair_request_approval → approval_route_step foreign key is tenant-composite (tenant_id, approval_route_id, approval_step_no); one index serves both the step and the route foreign keys.
  - No redundant foreign-key indexes are created.
  - The RR-DBD-001 approval inbox index is DEFERRED to the approval inbox ticket (Section 8), because no S1-007R query uses it.

#### DEC-PRE-S1-007R-07 — Admin Retry Routing
> A recovery command for routing failures only. ADMINISTRATOR only, tenant-scoped, If-Match required.

- **Target:** SUBMITTED, with an unresolved routing failure **or** never routed, and no assigned PENDING approval.
- It re-runs the same server-side rules; the caller never provides a route or an approver.
- **Success:** the approval row is created or updated with the assigned approver, the failure is cleared, SUBMITTED → UNDER_REVIEW, audit.
- **Failure:** stays SUBMITTED, the new failure is recorded, no partial assignment, Request No and SLA start unchanged.
- **Concurrency:** the request row is locked for the attempt, and UNIQUE(request, step) is the backstop, so there is no duplicate assignment or transition.
- **Lock wait (S1-007R pre-commit review, Portfolio Project Owner, 15 September 2026):** routing is serialized per Repair Request by a transaction-owned SQL Server application lock with a bounded wait (no in-memory lock, no spinning, no retry loop). A lock that is not granted in time writes nothing and returns the existing concurrency conflict (409, the same contract as the Submit duplicate-key lock); no new public status is introduced. Routing of other requests is never blocked, and genuine database failures are never converted into concurrency responses.
- **Errors:** cross-tenant → 404; wrong role → 403.
- No bulk re-routing, no background retry Worker.
- Configuration scope never grants Approve or Reject.

#### DEC-PRE-S1-007R-08 — Existing S1-007 SUBMITTED requests
> Left unchanged: no data migration, no startup routing, no fabricated routing-failure record. They count as "not yet routed" and are recoverable through DEC-PRE-S1-007R-07.

#### DEC-PRE-S1-007R-09 — Routing failure persistence
> 1. **No route or no valid route step:** no repair_request_approval row; the request stays SUBMITTED; a `REPAIR_REQUEST_ROUTING_FAILED` audit records the code.
> 2. **Route and step resolved but no approver assigned:** a repair_request_approval row with the route and step, `assigned_approver_id` null and `routing_failure_code` set; the request stays SUBMITTED.

- **Current-failure semantics (S1-007R pre-commit review, Portfolio Project Owner, 15 September 2026):** when a later attempt ends in a route-level failure, the earlier unassigned approver-level failure row is removed in the same transaction, so the current state never contradicts the latest failure.
  - Only a PENDING row with no assigned approver and a routing failure can be removed; an assigned or decided approval never is.
  - History is not lost: every attempt stays in the append-only `REPAIR_REQUEST_ROUTING_FAILED` audit with its code, route and step.
  - The current failure is the latest routing audit; the approval row, when present, always matches it.

- APR-004 approval_route_id and APR-005 approval_step_no stay NOT NULL. APR-011 is kept.
- **Codes:**
  - no approval row: ROUTE_NOT_FOUND, ROUTE_AMBIGUOUS, ROUTE_STEP_INVALID;
  - with an approval row: APPROVER_NOT_ELIGIBLE, APPROVER_NOT_FOUND, APPROVER_AMBIGUOUS.

#### DEC-PRE-S1-007R-10 — Administrator routing-issue list
> A **narrowly-scoped exception** to the S1-003 ADMINISTRATOR no-business-read rule, for routing operations only.

- **Returns:** a tenant-scoped, paged list of SUBMITTED requests that have no repair_request_approval yet or have an unresolved routing failure.
- **Fields returned:** repairRequestId, requestNo, siteId, requestCategoryCode, submittedAt, lastRoutingFailureCode, rowVersion.
- **Never returned:** description, requester or contact identity, attachments or their metadata, audit history, or other fields.
- **Query rules:** bounded page size, deterministic sort (submittedAt, id), tenant predicate in SQL, no N+1.
- Retry still requires If-Match.
- It is not a general Repair Request read permission and never grants Approve or Reject.

#### DEC-PRE-S1-008-01 — Segregation of duties for Approve/Reject
> A user must not Approve or Reject a Repair Request they created, even if the user also holds the APPROVER role.

- This is a **portfolio rule added beyond the baseline**; the baseline does not address self-decision.
- **Enforcement:**
  - approval routing never assigns created_by as the assigned approver (implemented in S1-007R, DEC-PRE-S1-007R-05);
  - S1-008 Approve/Reject must independently re-check the rule server-side;
  - a violation returns **403 ACCESS_DENIED**, recorded through the existing security logging where appropriate;
  - nothing relies on the frontend hiding actions;
  - tenant/Site scope behaviour is unchanged.
- **Tests required in S1-008:**
  - REQUESTER + APPROVER approving their own request → 403;
  - REQUESTER + APPROVER rejecting their own request → 403;
  - the same user approving another assigned request → allowed;
  - another assigned APPROVER deciding the request → allowed.
- **Status:** APPROVED. The routing portion is IMPLEMENTED in S1-007R; the Approve/Reject portion is IMPLEMENTED in S1-008 (commit a45bc3875398bf04e7b1e82c18ecb211aca007c6).

#### DEC-PRE-S1-008-02 — Return for Correction is a separate ticket
> S1-008 implements Review / Approve / Reject only. Return for Correction (ST-RR-006) and its resubmission semantics are deferred to a dedicated later ticket.

- **Not in S1-008:**
  - Return for Correction;
  - reopening edit permissions;
  - resubmit;
  - Request No behaviour on resubmit;
  - SLA restart/resume;
  - duplicate behaviour on resubmit.
- REJECTED and Return for Correction are never collapsed into one state or command.
- **Dependency:** the Return-for-Correction ticket depends on S1-007R routing and S1-008. It decides NB-3. It must also address re-routing after resubmit, because UNIQUE(repair_request_id, approval_step_no) has no cycle dimension today.
- **Status:** DEFERRED.

#### DEC-PRE-S1-008-03 — Approve/Reject source state
> Approve (ST-RR-004) and Reject (ST-RR-005) are accepted only while the Repair Request is **UNDER_REVIEW** with a PENDING approval step assigned to the caller. A decision on a SUBMITTED request returns **409 STATE_CONFLICT**.

- **Source:** Portfolio Project Owner, S1-008 pre-implementation review, 16 September 2026.
- **Why:** RR-STS-001 lists SUBMITTED and UNDER_REVIEW as sources. Only the assigned approver may decide, though (UC-RR-003; DEC-PRE-S1-007R-02), and S1-007R assigns an approver and moves SUBMITTED → UNDER_REVIEW in one transaction. A SUBMITTED request therefore never has an assigned approver.
- The transition matrix is not changed; the command guard is stricter.
- **Also recorded for S1-008 (no new decision):**
  - the decision actor and time are audit data (no approved_by/at or rejected_by/at columns);
  - the reject reason is stored in RR-016 `reject_reason` and APR-008 `decision_reason` (both nvarchar(1000), required on Reject) and as the audit reason;
  - the EV-SLA-005 SLA stop and decision notifications stay deferred with the SLA and notification scope.
- **Status:** APPROVED – IMPLEMENTED in S1-008 (commit a45bc3875398bf04e7b1e82c18ecb211aca007c6).

#### DEC-PRE-S1-009-01 — Cancel leaves approval rows unchanged
> Cancelling a Repair Request (ST-RR-007) does not change, delete or add any `repair_request_approval` row. An assigned PENDING step stays PENDING, and APPROVED/REJECTED decision rows and approver-level routing-failure rows keep their state.

- **Source:** Portfolio Project Owner, S1-009 pre-implementation review, 16 September 2026.
- **Why:** the baseline defines no approval-step effect for Cancel, and APR-007 has no CANCELLED value. Leaving the row avoids a baseline change and keeps the assignment history.
- **Consequences:**
  - the row becomes inert: Approve/Reject require UNDER_REVIEW and Admin Retry requires SUBMITTED, so both return 409 after Cancel;
  - a future approval inbox must filter by the Repair Request status as well as the approval status.
- **Also recorded for S1-009 (no new decision):**
  - Cancel is by the owning Requester only (ST-RR-007; UC-RR-004), from DRAFT/SUBMITTED/UNDER_REVIEW/APPROVED;
  - the reason is required and stored in RR-015 `cancel_reason`;
  - the SLA stop (EV-SLA-005 REQUEST_CANCELLED) stays deferred with `sla_record` (DEC-PRE-S1-007-12), and NTF-CANCEL with the notification scope.
- **Status:** APPROVED – IMPLEMENTED in S1-009 (uncommitted; commit hash to be recorded at commit).

---

## 8. Deferred Items (not approved requirements)

| Item | Status | Source |
|---|---|---|
| Category-specific evidence rules (category-required CLEAN evidence at Check-out) | DEFERRED — Work Order scope | BR-06, TC-WO-007 |
| Location master and the category-conditional Equipment/Location rule | DEFERRED | DEC-PRE-S1-007-05 |
| Contact name / phone / email snapshot fields on the Repair Request | DEFERRED — not in baseline, not approved | DEC-PRE-S1-007-04 |
| Category / Priority management screens (CRUD UI) | DEFERRED | DEC-PRE-S1-007-01/-02 |
| Approval routing engine (ST-RR-003) | No longer deferred — implemented as the S1-007R prerequisite (single step, exactly one assigned approver, Admin Retry) | DEC-PRE-S1-007-12; DEC-PRE-S1-007R-01..10 |
| Sequential multi-step approval routing (step_no > 1) | DEFERRED — S1-007R supports a single step only | DEC-PRE-S1-007R-03 |
| ACTIVE/inactive user-state check for the assigned approver | DEFERRED until REQ-FU-USR-001 (Section 8.1) | DEC-PRE-S1-007R-05 |
| Approver reassignment / manual approver selection | DEFERRED — not in baseline; the caller never chooses a route or approver | DEC-PRE-S1-007R-07 |
| Automatic bulk re-routing after configuration changes; background routing retry Worker | DEFERRED — recovery is the Admin Retry Routing command only | DEC-PRE-S1-007R-07 |
| Routing notifications (NTF-SUBMITTED to assigned approver; routing-failure notification) | DEFERRED — notification/outbox scope | DEC-PRE-S1-007-12; DEC-PRE-S1-007R-01 |
| Approval inbox list endpoint for approvers (UI-020) | DEFERRED — S1-008 or list ticket | DEC-PRE-S1-007R-10 |
| Approval inbox index `IX_repair_request_approval_inbox` (assigned_approver_id, status, routed_at; RR-DBD-001) | DEFERRED — created by the approval inbox ticket together with its query; no S1-007R query uses it (Portfolio Project Owner, S1-007R pre-commit review) | DEC-PRE-S1-007R-06 persistence note |
| S1-008 Approve / Reject (ST-RR-004/005) | No longer deferred — IMPLEMENTED in S1-008 (a45bc38); assigned approver only, self-decision forbidden, UNDER_REVIEW only | DEC-PRE-S1-008-01; DEC-PRE-S1-008-03 |
| Decision side effects: EV-SLA-005 SLA stop on Reject; NTF decision notifications (Coordinator on Approve, Requester on Reject) | DEFERRED — SLA calculation and notification/outbox scope | DEC-PRE-S1-007-12; DEC-PRE-S1-008-03 |
| S1-009 Cancel (ST-RR-007) | IMPLEMENTED in S1-009 (uncommitted) — owning Requester only; DRAFT/SUBMITTED/UNDER_REVIEW/APPROVED → CANCELLED; approval rows unchanged | DEC-PRE-S1-009-01 |
| Cancel side effects: EV-SLA-005 SLA stop (stop_reason REQUEST_CANCELLED); NTF-CANCEL | DEFERRED — `sla_record` / SLA scope and notification/outbox scope | DEC-PRE-S1-007-12; DEC-PRE-S1-009-01 |
| Return for Correction (ST-RR-006) and resubmission semantics | DEFERRED — dedicated later ticket; never merged with REJECTED | DEC-PRE-S1-008-02; NB-3 |
| Submitted notification / outbox implementation (NTF-SUBMITTED) | DEFERRED — later Sprint 1 ticket | DEC-PRE-S1-007-12 |
| Production malware scanning provider | DEFERRED — Security Hardening / Deployment | DEC-PS1-005 |
| Production background malware-scan Worker | DEFERRED — the Development/Testing-only runner of DEC-PRE-S1-007-08 does not satisfy or replace it | DEC-S1-006-F2 (clarified 14 September 2026); DEC-PRE-S1-007-08 |
| ACTIVE/inactive user-state check for the request contact (RR-018) — only this portion; existence, tenant and Site-scope validation remain enforced | DEFERRED until REQ-FU-USR-001 (Section 8.1) — the User model has no authoritative business lifecycle/status; Identity lockout is not business status | DEC-PRE-S1-007-04 (formal deferral, 15 September 2026) |
| Attachment removal | DEFERRED | DEC-S1-006-F3 |
| Upload rate limiting | DEFERRED | Section 4 item 10 (CORS/rate-limit INFORMATIONAL) |
| Sprint 2 team / assignment scope (Work Order, Visit, Session policies) | DEFERRED — Sprint 2 | `AuthorizationPolicies` note; RR-REQ-001 §13 |
| Transactional-record deactivation guard | OPEN / DEFERRED | Section 4 item 13; DEC-S1-004-D4 |
| SLA due-time calculation, risk threshold, breach evaluation, working-hours/calendar calculation and escalation (incl. `sla_record` creation, EV-SLA-001..005 calculations, NTF-SLA-RISK / NTF-SLA-BREACH, SLA Override) | DEFERRED — SLA Policy/Monitoring scope | DEC-PRE-S1-007-12 |

### 8.1 Follow-up Requirements

| ID | Requirement | Due | Status | Source |
|---|---|---|---|---|
| REQ-FU-USR-001 | **User lifecycle/status.** The system needs an authoritative User business lifecycle/status model before ACTIVE-contact validation (RR-018, DEC-PRE-S1-007-04) can be enforced. The model must be separate from ASP.NET Core Identity lockout. IsActive/UserStatus is **not** implemented in S1-007; this remains a formal follow-up requirement. **To be decided by an approved decision (not decided here):** states and transitions, who may change a user's status, reason and audit, and the effect on existing references. **Enforcement points to evaluate:** request-contact eligibility at Draft save and Submit (RR-018, closing the DEC-PRE-S1-007-04 deferral), authentication and token refresh, and the Work Order acceptance contact (CAC-005/CAC-009, Sprint 2). Automated tests are required | Before final Sprint 1 security/UAT readiness or production readiness, whichever comes first | OPEN — not in S1-007 | DEC-PRE-S1-007-04 (formal deferral, 15 September 2026); NB-10 |

---

## 9. Non-Blocking Notes and Open Items (v1.2)

| # | Item | Classification |
|---|---|---|
| NB-1 | 24-hour duplicate window: an existing request counts when its `submitted_at` ≥ the current Submit time − 24h. `submitted_at` is the only timestamp available for the BR-14 comparison | NON-BLOCKING (interpretation to confirm in S1-007 tests) |
| NB-2 | CONVERTED counts as active only "while its linked Work Order is not CLOSED/CANCELLED" (D-12). No Work Orders exist in Sprint 1, so every CONVERTED request counts until Work Order implementation | NON-BLOCKING |
| NB-3 | Resubmit after Return for Correction: Request No stays unchanged (BR-01 immutable). SLA restart/continuation semantics on resubmit are not decided | NON-BLOCKING — decide in the Return-for-Correction ticket |
| NB-4 | Documentation previously missing from RR-DEC-001: S1-004 D4 and S1-006 F3 are now recorded (Section 6). The PDF baseline wording lag (Section 2/4) is unchanged | NON-BLOCKING (documentation lag) |
| NB-5 | Whether an **additional FAILED attachment** blocks Submit. Previously MUST RESOLVE AT S1-007 START (commit a10cab8). **Final S1-007-start decision** by the Portfolio Project Owner (14 September 2026): FAILED attachments do not block Submit; they do not satisfy mandatory evidence, are not usable and cannot be downloaded (DEC-PRE-S1-007-07) | **RESOLVED** |
| NB-6 | Read-only lookup endpoints for Category, Priority and eligible contacts are not designed or decided. No endpoint exists | OPEN — S1-007 design item |
| NB-7 | TC-CUST-\*, TC-SITE-\*, TC-EQP-\*, TC-AUTH-\* and the ST-CUST/SITE/EQP transition tables (Section 2) are still not authored into RR-TC-001 / RR-STS-001. Automated tests exist | NON-BLOCKING (documentation lag) |
| NB-8 | S1-002 decision labels (M1, M2, M3, M6, B4) and S1-003 decisions 1–4 exist only as code comments and are not recorded in this register | NON-BLOCKING (outside v1.2 scope) |
| NB-9 | **SLA values for the SLA Policy / Monitoring scope.** The approved baseline defines the Resolution SLA duration (BR-12 / FR-08 "24h 24/7 from SUBMITTED"; EV-SLA-001; SLA-006) and risk threshold (D-13 / BR-17 "effective_due_at − 4 hours"; SLA-008), independently of the Pre-S1-007 session. They are retained unchanged and not applied in S1-007 (DEC-PRE-S1-007-12). Notes for that later scope: (a) the baseline PDFs still carry "approval pending" wording, which DEC-PS1-016 supersedes for governance classification; (b) a working-hours/calendar model would differ from BR-12 "24/7" and would need a Change Request if adopted; (c) escalation beyond NTF-SLA-RISK / NTF-SLA-BREACH is not defined in the baseline | NON-BLOCKING for S1-007 — confirm at the SLA Policy / Monitoring scope |
| NB-10 | **ACTIVE request contact — formally deferred** (Portfolio Project Owner, 15 September 2026). Baseline RR-018 "Active contact in selected Site" requires an active user, but the User model has no authoritative business lifecycle/status, and Identity lockout is not one. S1-007 enforces, at Draft save and at Submit: existing user, same tenant, and business Site scope on the selected Site (non-ADMINISTRATOR role + user_site_scope). Cross-tenant and out-of-scope contacts are rejected, and the client-provided id never bypasses backend validation. Only the ACTIVE/inactive user-state check is deferred. It must not be reported as enforced until REQ-FU-USR-001 is delivered. No IsActive/UserStatus column is added in S1-007 | DEFERRED — DEC-PRE-S1-007-04; follow-up REQ-FU-USR-001 (Section 8.1) |

---

## 10. Decision Traceability (v1.2)

| Decision | Business Rule / Baseline | State Transition | API (RR-API-001 / RR-API-001-ADD) | Test Case (RR-TC-001) | Automated evidence |
|---|---|---|---|---|---|
| DEC-S1-004-D1 | BR-04 reason/audit convention; DEC-PS1-014 | ST-CUST/SITE/EQP INACTIVE→ACTIVE (register-required, not authored) | CUST/SITE/EQP-API-005 | TC-CUST/SITE/EQP-\* (not authored) | CustomerEndpointsTests, CustomerServiceTests |
| DEC-S1-004-D2 | DEC-PS1-002/003/013 | ST-SITE/EQP activate guard | SITE-API-005, EQP-API-005 | TC-SITE/EQP-\* (not authored) | SiteAndEquipmentServiceTests, SiteAndEquipmentEndpointsTests |
| DEC-S1-004-D3 | DEC-PS1-013/015 | — | CUST/SITE/EQP-API-004 | TC-CUST/SITE/EQP-\* (not authored) | SiteAndEquipmentServiceTests, CustomerServiceTests |
| DEC-S1-004-D4 | DEC-PS1-013; Section 4 item 13 | ST-CUST/SITE deactivate guard | CUST-API-006, SITE-API-006 | TC-CUST/SITE-\* (not authored) | Master-data deactivation tests |
| DEC-S1-005-E1 (SUPERSEDED) | FR-01; RR-DD-001 RR-007/008/010/018 | ST-RR-001 | RR-API-001/002 | TC-RR-001 | RepairRequestDraftEndpointsTests |
| DEC-S1-005-E2 | BR-03; FR-01 | ST-RR-001 | RR-API-001/002 | TC-RR-001; TC-SEC-002 | RepairRequestDraftServiceTests, RepairRequestDraftEndpointsTests |
| DEC-S1-005-E3 | BR-16; D-11; S1-003 scope | ST-RR-001 | RR-API-002/004 | TC-SEC-001 | RepairRequestDraftStoreTests, RepairRequestDraftEndpointsTests |
| DEC-S1-006-F1 | RR-REQ-001 §9 File NFR | — | FILE-API-001/003 | TC-RR-002 | AttachmentEndpointsTests |
| DEC-S1-006-F2 | DEC-PS1-005; RR-REQ-001 §9 | FileAsset PENDING→CLEAN/FAILED (DEC-PS1-005 optional table) | FILE-API-002 (non-CLEAN → 409) | TC-RR-002 | FileScanServiceTests |
| DEC-S1-006-F3 | — (capability absent) | — | none (no removal endpoint) | — | Route catalog |
| DEC-S1-006-F4 | RR-DD-001 ATT-014; BR-16 | — | FILE-API-001/002/003 | TC-RR-002; TC-SEC-001 | RepairRequestAttachmentServiceTests, AttachmentEndpointsTests |
| DEC-S1-006-SK | RR-DD-001 FAS-007; RR-ARCH-001 §11 | — | FILE-API-001 | TC-RR-002 | RepairRequestAttachmentServiceTests |
| DEC-S1-006-PG | RR-API-001 §7/§9 | — | FILE-API-003 | TC-RR-002; TC-SEC-001 | AttachmentEndpointsTests |
| DEC-PRE-S1-007-01/-02/-03 | FR-01/02; RR-DD-001 RR-008/010, §2; BR-03 | ST-RR-001/002 | RR-API-001/002/005 (ADD §4.1, §5) | TC-RR-003 | RequestNumberAndLookupTests (Lookups_AreTenantScopedAndActiveByDefault, Lookups_RejectInvalidValues); MigrationSeedTests.UpgradingAnExistingDatabase_SeedsLookupsOncePerTenantWithUsers; RepairRequestSubmitStoreTests.LookupSeeding_IsIdempotent_ConcurrencySafe_AndCoversTenantsWithUsers; RepairRequestSubmitEndpointsTests (Draft_AcceptsLookupsAndContact_StoresCanonicalCodes_AndRejectsInvalidSelections, Submit_WhenLookupsBecameInactive_OrContactLostSiteAssignment_Returns422) |
| DEC-PRE-S1-007-04 | RR-DD-001 RR-018; BR-16 (ACTIVE portion formally deferred → REQ-FU-USR-001) | ST-RR-001/002 | RR-API-001/002/005 (ADD §5) | TC-RR-003; TC-SEC-001 | Existence/tenant/Site scope: RepairRequestDraftStoreTests.DraftSelection_ContactMustHoldABusinessRoleAndBeAssignedToTheSelectedSite; RepairRequestSubmitEndpointsTests (Draft_AcceptsLookupsAndContact_StoresCanonicalCodes_AndRejectsInvalidSelections, Draft_ContactFromAnotherTenant_IsRejected_IdenticallyToUnknown_AndNothingIsCreated, Submit_WhenLookupsBecameInactive_OrContactLostSiteAssignment_Returns422); PersistenceConstraintTests.RepairRequest_ContactFromAnotherTenant_IsRejected. ACTIVE status: none (deferred) |
| DEC-PRE-S1-007-05 | RR-DD-001 RR-007; RR-REQ-001 §12 | ST-RR-001 | RR-API-001/002 (ADD §5) | TC-RR-003 | No dedicated test: `RepairRequestDraftRequest` has no `locationId` member and no command sets `location_id`; duplicate matching requires `location_id` null (RepairRequestSubmitStoreTests.DuplicateCount_MatchesSiteCategoryAndExactEquipment_InTheActiveSetWithinTheInclusiveWindow) |
| DEC-PRE-S1-007-06/-07 | RR-REQ-001 §9 (CLEAN only) + portfolio rule; DEC-S1-006-F2 | ST-RR-002 guard (failure: stays DRAFT, no Request No, no SLA start) | RR-API-005 (ADD §4.1, §5); FILE-API-002 (CLEAN only) | TC-RR-002; TC-RR-003 | FILE-API-002 behaviour: AttachmentEndpointsTests; Submit guard: RepairRequestSubmitEndpointsTests (Submit_WithoutCleanPhoto_Returns422OnAttachments_AndStaysDraft, Submit_CleanPhotoWithAnAdditionalPendingOrFailedAttachment_Succeeds); RepairRequestSubmitStoreTests.CleanPhoto_OnlyCleanJpegOrPngOfTheRequestTenantCounts; RepairRequestSubmitServiceTests.Submit_WithoutCleanPhoto_Returns422OnAttachments_WithoutDuplicateQuery |
| DEC-PRE-S1-007-08 | DEC-PS1-005; DEC-S1-006-F2 (clarified) | FileAsset PENDING→CLEAN/FAILED (Development/Testing only) | FILE-API-001/002; RR-API-005 | TC-RR-002 support | DevelopmentMalwareScanningTestingTests, DevelopmentMalwareScanningDevelopmentTests, DevelopmentMalwareScanningProductionTests |
| DEC-PRE-S1-007-09/-10 | BR-14; D-12 | ST-RR-002 guard | RR-API-005 (ADD §4.1, §5) | TC-RR-004 | Concurrency: RepairRequestSubmitStoreTests (ConcurrentSubmitsOfDifferentDraftsWithTheSameDuplicateKey_ExactlyOneSucceeds_TheOtherGetsTheDuplicateWarning, ConcurrentSubmitsOfNonMatchingDrafts_DoNotWaitOnEachOthersDuplicateLock, DuplicateKeyLockTimeout_Returns409_WritesNothing_AndTheDraftCanBeSubmittedAfterwards, FailureInsideTheTransaction_RollsBackAllocationTransitionAndAudit_AndReleasesTheDuplicateLock, DuplicateLockResource_IsTheExactDuplicateKey_AndEmptyEquipmentNeverSharesAKeyWithSelectedEquipment); RepairRequestSubmitEndpointsTests (Submit_Duplicate_WithoutReason_Returns422CountOnly_ThenWithReasonSucceeds_AndAuditsTheReason, Submit_EmptyEquipmentMatchesOnlyEmptyEquipment_AndNullIsNeverAWildcard, Submit_RejectedOrCancelledRequestsAndOtherTenantsDoNotCount_ButUnderReviewDoes); RepairRequestSubmitStoreTests.DuplicateCount_MatchesSiteCategoryAndExactEquipment_InTheActiveSetWithinTheInclusiveWindow; RepairRequestSubmitServiceTests (Submit_DuplicateWithoutReason_Returns422WithCountOnly_AndStaysDraftWithoutNumberOrSlaStart, Submit_DuplicateWithReason_Succeeds_StoresAndAuditsTheTrimmedReasonAndCount, Submit_ReasonWithoutDuplicate_Succeeds_AndTheReasonIsIgnored, Submit_ContinuationReasonLongerThanLimit_Returns422_WithoutDuplicateQuery) |
| DEC-PRE-S1-007-11 | BR-01; RR-DD-001 RR-003 | ST-RR-002 side effect | RR-API-005 (ADD §4.1, §5) | TC-RR-003; TC-SEC-002 | RepairRequestSubmitStoreTests (Allocation_ConcurrentSubmitsInOneTenantYear_ProduceUniqueGaplessNumbers, Allocation_SequencesAreSeparatePerTenantAndPerYear, StaleSecondSubmitOfTheSameDraft_Returns409_AndRollsBackItsAllocation, FailureInsideTheTransaction_RollsBackAllocationTransitionAndAudit); RepairRequestSubmitEndpointsTests (Submit_NumbersAreSequentialPerTenant_AndAnotherTenantStartsAtOne, Submit_TwoConcurrentRequestsWithTheSameETag_ExactlyOneSucceeds_WithOneNumberAndOneAudit, Submit_AlreadySubmitted_Returns409StateConflict_AndKeepsTheOriginalNumber); RequestNumberAndLookupTests (Format_UsesYearAndSixDigitSequence, Format_RejectsOutOfRangeInput_AndFailsClosedWhenSequenceIsExhausted); RepairRequestSubmitTests.Submit_WhenRequestNoAlreadyAssigned_IsRejected_RequestNoIsImmutable |
| DEC-PRE-S1-007-12 | FR-02; RR-DD-001 RR-014 ("SLA start"); BR-18 | ST-RR-002 (SLA start timestamp only); SLA calculation / EV-SLA-001..005 and ST-RR-003 deferred | RR-API-005 (ADD §4.1, §5) | TC-RR-003/004 (TC-SLA-001..003 deferred) | RepairRequestSubmitEndpointsTests.Submit_ValidDraft_Returns200Submitted_WithRequestNo_SlaStartMarker_ETag_AndAudit; RepairRequestSubmitServiceTests.Submit_ValidDraft_GeneratesRequestNo_SetsSubmittedAndSlaStartMarker_AndAuditsOnce; RepairRequestSubmitTests.Submit_CompleteDraft_BecomesSubmittedWithNumberActorAndSlaStartMarker; RepairRequestSubmitStoreTests.SubmitService_EndToEnd_IsBounded_AndFailedValidationLeavesNoTrace (no Request No / SLA start on failure) |
| DEC-PRE-S1-007R-01/-02/-03 | ST-RR-003; UC-RR-002/003; RR-DD-001 ARC-005..007, APR-006 | ST-RR-003 SUBMITTED→UNDER_REVIEW (System); failure stays SUBMITTED | RR-API-005 response status (ADD §4.1) | TC-RR-005 | ApprovalRoutingRulesTests; RepairRequestRoutingServiceTests; ApprovalRoutingTests (Submit_WithRoleOnlyTenantDefaultRoute_AndOneEligibleApprover_AssignsIt_AndMovesToUnderReview, Submit_RouteResolvedWithTwoEligibleApprovers_WritesAFailureRowWithoutApprover_AndStaysSubmitted); RepairRequestSubmitServiceTests (routing hook, Submit_WhenRoutingAndReadBackBothThrow_ReturnsTheCommittedSubmittedState, Submit_WhenRoutingDoesNotCompleteAndReadBackThrows_NeverClaimsUnderReview); PostSubmitRoutingFailureEndpointsTests.Submit_WhenPostCommitRoutingAndReadBackThrow_Returns200Submitted_AndTheSubmitIsNotRepeated; RepairRequestRoutingServiceTests and ApprovalRoutingTests (System routing actor + initiatedBy); ApprovalRoutingEndpointsTests.Submit_WithAConfiguredRoute_ReturnsUnderReview_WithAFreshETag_AndNeverAssignsTheRequesterApprover |
| DEC-PRE-S1-007R-04/-06 | RR-DD-001 ARC-001..008; RR-DBD-001 approval_route(_step); ERD-DR-02 | — | ROUTE-API-001..005 (ADD §2, §4.6) | — | ApprovalRouteServiceTests; ApprovalRoutingEndpointsTests (Administrator_CreatesDeactivatesAndActivatesARoute_WithETags, RouteCreate_ValidatesRoleApproverUserAndOneActiveRoutePerKey, RouteOfAnotherTenant_IsNotFound); ApprovalRoutingTests.Database_AllowsOneActiveRoutePerSiteAndPerTenantDefault_ButKeepsInactiveHistory; PersistenceModelTests (ApprovalRoute_FilteredUniqueIndexes_AllowOneActiveRoutePerSiteAndPerTenantDefault, ApprovalRouting_KeysCoverForeignKeys_WithoutRedundantIndexes) |
| DEC-PRE-S1-007R-05 + DEC-PRE-S1-008-01 (routing portion) | UC-RR-003 "Role + route + site scope"; portfolio SoD rule | ST-RR-003 guard | — | TC-SEC-001 | ApprovalRoutingRulesTests (creator exclusion); ApprovalRoutingTests (SiteRoute_TakesPrecedence_AndItsSpecificApproverWithoutSiteScope_IsNotEligible_NoFallback, RequesterHoldingApprover_IsNeverAssignedTheirOwnRequest_AndRetryAssignsAnotherApprover, SpecificApproverWithoutApproverRole_AtRoutingTime_IsNotEligible_WritesFailureRow_AndStaysSubmitted); ApprovalRoutingDomainTests; S1-008 SoD: RepairRequestDecisionServiceTests.CreatorHoldingApprover_IsAccessDenied_EvenWhenTheStepIsAssignedToThem, RepairRequestDecisionTests.CreatorHoldingApprover_WithTheStepForcedToThem_IsAccessDenied, RepairRequestDecisionEndpointsTests.RequesterHoldingApprover_ApprovingOrRejectingTheirOwnRequest_Gets403 |
| DEC-PRE-S1-007R-07/-08/-09/-10 | RR-UI-001 "authorized staff sees routing issue"; RR-DD-001 APR-011 | ST-RR-003 retry (SUBMITTED only) | ROUTING-API-001/002 (ADD §2, §4.6) | TC-RR-005; TC-SEC-001/002 | RepairRequestRoutingServiceTests (retry cases); ApprovalRoutingTests (RoutingIssues_ListOnlyUnroutedAndFailedSubmittedRequests_OfTheTenant_AndRetryRoutesALegacyRequest, ConcurrentAdminRetries_OfOneRequest_ExactlyOneRoutes_WithOneAssignmentAndOneAudit, SubmitRouting_RacingAdminRetry_ExactlyOneRoutes_WithOneAssignmentAndOneAudit, Retry_AfterRouteDeactivated_RemovesStaleApproverFailureRow_ListShowsRouteNotFound_HistoryKeptInAudit, Database_RejectsInvalidStepsAndApprovalRows incl. cross-tenant route/step); RepairRequestRoutingServiceTests.Retry_RouteLevelFailureAfterApproverFailure_RemovesTheStaleRow_AndAuditsTheNewCode; ApprovalRoutingDomainTests.Approval_OnlyAnUnassignedRoutingFailure_IsDiscardable; ApprovalRoutingEndpointsTests (UnroutedSubmit_IsListedWithMetadataOnly_AndAdminRetryRoutesItAfterConfiguration, BusinessRoles_Get403_OnRouteConfigurationAndRoutingRecovery, Administrator_StillHasNoRepairRequestDetail_OrSubmit) |
| DEC-PRE-S1-008-02 | UC-RR-003; ST-RR-006; NB-3 | ST-RR-006 (DEFERRED ticket) | RR-API-008 (not implemented) | TC-RR-008 | — (Return-for-Correction ticket) |
| DEC-PRE-S1-008-01/-03 (Approve/Reject) | UC-RR-003; RR-REQ-001 §13; RR-DD-001 RR-016, APR-007..010 | ST-RR-004 / ST-RR-005 (UNDER_REVIEW only) | RR-API-006 / RR-API-007 (ADD §2, §4.7) | TC-RR-006/007; TC-SEC-001 | RepairRequestDecisionDomainTests; RepairRequestDecisionServiceTests; RepairRequestDecisionTests (atomic persistence, ConcurrentDecisions_OfOneRequest_ExactlyOneWins_WithOneDecisionAndOneAudit, GenuineDatabaseFailure_RollsBackTheWholeDecision_AndReleasesTheLock, DecidedRequest_CannotBeDecidedAgain_NorReroutedByAdminRetry); RepairRequestDecisionEndpointsTests |
| DEC-PRE-S1-009-01 (Cancel) | UC-RR-004; RR-DD-001 RR-015; BR-04/BR-11 | ST-RR-007 (DRAFT/SUBMITTED/UNDER_REVIEW/APPROVED → CANCELLED) | RR-API-009 (ADD §2, §4.8) | TC-RR-009; TC-SEC-001 | RepairRequestCancelDomainTests; RepairRequestCancelServiceTests; RepairRequestCancelTests (allowed states with approval rows unchanged, CancelRacingAnotherCommandOnUnderReview_ExactlyOneWins, CancelRacingAdminRetryRouting_ExactlyOneWins, CancelCommittedFirst_ThenPostSubmitRouting_IsConflict_AndWritesNoAssignment, RoutingSuccessCommittedFirst_ThenCancelWithTheSubmittedETag_IsConflict, CancelWhileTheReviewLockIsHeld_IsConcurrencyConflict_AndWritesNothing, GenuineDatabaseFailure_RollsBackTheCancel_AndReleasesTheLock); RepairRequestCancelEndpointsTests |

**v1.2 conclusion:**
- This revision records existing approved decisions only. No locked business semantics (State, Role, Guard, SLA, Permission, Notification Recipient) is changed.
- DEC-S1-005-E1 is marked SUPERSEDED, not deleted.
- Deferred items stay deferred (Section 8). Open items are listed in Section 9.
