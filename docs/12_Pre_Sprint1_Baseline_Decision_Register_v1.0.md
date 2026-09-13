RR-DEC-001

# Repair Request — Pre-Sprint-1 Baseline Decision Register

Portfolio Implementation Decisions + Cross-Reference + Required Baseline Updates

## Document Control

| Field | Value |
|---|---|
| Document ID | RR-DEC-001 |
| Version | 1.1 – Baseline Approved (Round 2) |
| Status | Approved for Portfolio Development |
| Revision Date | 13 September 2026 |
| Supersedes | RR-DEC-001 v1.0 — extends with DEC-PS1-013..016 (Round 2 closure) |
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
| 13 | Whether Customer/Site deactivation guard should also check for *open/active transactional records* (e.g. in-progress Repair Requests under a Site), beyond just active child master-data rows | **OPEN — not decided; does not block Sprint 1 start** | DEC-PS1-013 explicitly guards on active **Site**/active **Equipment** counts only; it does not address in-flight Repair Requests/Work Orders. Not assumed here — flagged for a future decision if needed |

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
