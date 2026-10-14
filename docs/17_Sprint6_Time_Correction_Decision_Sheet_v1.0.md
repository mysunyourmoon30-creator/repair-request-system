RR-DEC-S6-TC

# Time Correction (UC-TIME-001) — Sprint 6 Decision Sheet

Documentation only. This sheet records the proposals for the Time Correction feature and the **owner's partial approval of 13 October 2026** (§1A). Only the rules listed in §1A are approved, and each is mapped to its proposal ID; **every other proposal stays `PROPOSED_DECISION` / `PENDING_OWNER_APPROVAL` or `UNRESOLVED`** and is not an approved rule. The sheet changes no code, test, schema, migration or configuration, and no baseline PDF.

## 1. Document Control

| Field | Value |
|---|---|
| Document ID | RR-DEC-S6-TC |
| Version | 1.1 — owner partial approval recorded (13 October 2026); 1.0 was the PROPOSED draft |
| Status | **PARTIALLY APPROVED (13 October 2026).** Approved: the items of §1A (Backend Core only). Not approved and not to be implemented: everything marked PENDING_OWNER_APPROVAL or UNRESOLVED below. |
| Revision Date | 13 October 2026 (v1.0 draft and v1.1 approval reconciliation, the same day) |
| Timeline slice | 14 October 2026 — P11 / §1A item 11 implemented in the Sprint 6 timeline ticket on `feature/s6-time-correction-timeline` (read model only; `docs/13` §4.26, `docs/15` D2/§4.1/§9). Angular, the Team Lead UI and NTF-TIME remain deferred. Query evidence and the open at-scale limitation: `docs/15` §14. |
| Baseline commit | `48d45cce2448dcc7dcf23408d757ae52b2b0f17b` (`main`; merge of PR #26; post-merge CI green) |
| Reference (not authority) | local branch `feature/s6-time-correction-pending-scope`, tip `0bf39cfa2ed645399f285056c2d7c2d6ce1fc8b3` (one commit, merge-base `67bac74a`, S3-004). It is read only through `git show`/`git diff`; it is not merged, rebased or cherry-picked by this sheet. |
| Extends / cross-references | RR-REQ-001 v1.6 (`01`), BR-RR-BASELINE v1.6 (`02`), RR-STS-001 v1.6 (`03`), UC-RR-001 v1.7 (`04`), RR-DD-001 v1.4 (`05`), RR-TC-001 v1.3 (`06`), RR-ERD-001 (`07`), RR-DBD-001 (`08`), RR-API-001 v1.2 (`09`), RR-UI-001 v1.2 (`10`), `docs/12`, `docs/13` (§4.12–§4.14, §4.19, §4.20, §4.26, §4.28), `docs/15` (timeline), `docs/16` (Service Report, disclosure) |
| Approval | Portfolio Project Owner, 13 October 2026 — approval limited to §1A; "not an approval of P1–P14 as a whole" |
| Audience | Portfolio Project Owner, SA, Backend/Frontend Developer, Tester |

**Revision rule (same as `docs/12`, `docs/14`–`docs/16`).** This document does not change locked business semantics and modifies no baseline PDF (`docs/00`–`docs/11`, RR-ARCH-001). Baseline statements are cited by document and ID; `docs/02` and `docs/01` print their BR tables with scrambled column layout in the extracted text, so rules are cited by their text and the **BR numbers differ between documents** (`docs/01` calls the four-type rule BR-14 and the duration-facts rule BR-18; `docs/04` and `docs/06` trace UC-TIME-001 to BR-11/BR-13). That numbering inconsistency is itself recorded as UNRESOLVED (§3, row R18); it does not change any rule's meaning.

**Classification vocabulary.** `BASELINE_RULE` — stated by a baseline PDF. `APPROVED_DEVIATION` — an owner-approved decision recorded in `docs/12`, `docs/13`, `docs/15` or `docs/16`. `CODE_FACT` — what `main` does today (evidence, not a rule). `PROPOSED_DECISION` — new, owner status `PENDING_OWNER_APPROVAL`. `UNRESOLVED` — evidence is insufficient; no rule is proposed beyond naming the choice.

## 1A. Owner approval of 13 October 2026 — mapped to proposal IDs

The owner approved **only** the rules below and implementation of the **Backend Core** ticket (request, scoped list, approve/reject, pause targeting, state and chronology guards, atomic updates, audit, EF configuration, migration). Angular and timeline implementation are **not** part of that ticket. Where a proposal differed from the order, this sheet follows the order (see "Amended" column).

| # | Approved rule (owner order) | Proposal IDs | Amended against the draft |
|---|---|---|---|
| 1 | The four baseline correction types; exact pause targeting through `workSessionPauseId` | P1 (R1) | none |
| 2 | Update the pause history and mirror the `work_session` columns only where applicable | P1 | none |
| 3 | Request and Approve are prohibited when the Work Order is CLOSED or CANCELLED; every other Work Order state is permitted | P4, P5a (option A) | P5a is no longer UNRESOLVED: option A |
| 4 | Reject of a pending request stays permitted after CLOSED/CANCELLED and changes no time fact | P4 | the "Reject-on-terminal" question is answered: allowed |
| 5 | No self-decision (requester ≠ decider, even for a multi-role user) | P7 | approved; the baseline still states no such rule (the owner's decision is the source) |
| 6 | One PENDING correction per logical target: session + correction type + pause id where applicable | P8 | the uniqueness is enforced by the database atomically, including the nullable pause target |
| 7 | The new value must not exceed the server UTC now; chronology is checked at request **and** at approval | P2, P2a | P2a answered: bound = server UTC now (equal allowed); **no** Visit-window validation |
| 8 | No SLA recalculation, no cost change, no approval reset, no auto-reopen | P5, P10 | none |
| 9 | The Service Report stays a live read model and shows corrected facts | P5 (R13) | none |
| 10 | NTF-TIME is deferred (nothing is sent) | P12 | becomes an APPROVED_DEVIATION |
| 11 | Timeline will use the existing scope, actor and allowlisted before/after values; no raw reason | P11, P11a | approved in principle; **implemented in the Sprint 6 timeline ticket (14 October 2026)**; P11a answered: omit the reason |
| 12 | Team Lead requests remain site-scoped; the Angular entry point follows in a later ticket | P6, P14 | the Team Lead Angular surface stays UNRESOLVED (later ticket) |
| 13 | Concurrency: If-Match on decisions; compare the current target value with the recorded `old_value`; atomic pending uniqueness; re-validate chronology against the current affected intervals; read the Work Order state fresh under a transaction lock compatible with Close/Cancel and keep locks until apply + audit + decision commit; prove both race orders over independent SQL connections; decision audit carries the session id and the pause id where applicable; keep the PR #26 LocalDB coordination | P2, P8, P9, P13, P10 | none |
| 14 | `service_visit.completed_at` is synchronized **only** when existing code proves the corrected Check-out is its authoritative source; another session's completion is never overwritten; if the mapping is ambiguous the precise gap is reported before implementing it | P3 | conditional approval — see the evidence note below |
| 15 | A `newValue` equal to the current target value is refused with `422` (field `newValue`), for all four types, with no correction and no audit row written | P2 / §5.3 | approved 14 October 2026 (review flag a) |
| 16 | TIME-API-001 returns `201` with the body and the correction's `ETag`; **no `Location` header** and no single-item GET route is added | TIME-API-001 | approved 14 October 2026 (review flag b) |

**Evidence note for item 14 (P3).** Existing code: `completed_at` is written only by `ServiceVisit.CheckOut` through `WorkSessionService` at Check-out (`WorkSessionService.cs:154-155`, `ServiceVisit.cs:217-227`); the Visit must be SCHEDULED to Check-in (`TechnicianCheckInService`), so under the current flow a Visit has one Work Session. The implementation therefore synchronizes only when **all** of these hold at approval time: the Visit is COMPLETED, `completed_at` equals the correction's recorded `old_value` (the same instant the Check-out wrote), and the Work Session is the only session of that Visit. Otherwise `completed_at` is left unchanged and the gap is reported (the Work Session fact is still corrected). Whether a future multi-session Visit needs another rule is UNRESOLVED.

**Not approved by the order (remain PENDING_OWNER_APPROVAL or UNRESOLVED):** P2's exact interval rules beyond "chronology re-checked against the current affected intervals" are implemented as written in P2 because item 13 requires them; P13 follows item 13; any timeline or Angular behaviour is deferred (P11, P14); P9's lock mechanism is implemented as a row lock because item 13 requires a lock compatible with Close/Cancel; Q9 (BR numbering) stays UNRESOLVED. **Owner decisions of 14 October 2026 on the two review flags (APPROVED):** (a) the no-op guard stays — a `newValue` equal to the current target value is refused with `422` (field `newValue`) for all four types and writes no correction and no audit row (§5.3); it is an owner-approved technical validation, not a baseline rule; (b) TIME-API-001 returns `201` with the response body and the `ETag` of the new correction (the token for the decision's `If-Match`) and **no `Location` header**, because this ticket has no single-item GET route and none is added.

## 2. Scope, exclusions and requirement IDs

**Requirement IDs (real, from the baseline).** FR-06, FR-08; BR-05 (reason + audit), BR-04 (server-side validation); Time Correction rules in `docs/01` (supported types; Technician/Team Lead request; Supervisor decides; "changes work-duration facts only"; SLA Override separate) and `docs/02` ("Time Correction no SLA recalc"); UC-TIME-001; ST-TC-001..003; TC-TIME-001/002; data dictionary TC-001..TC-012 and WS-006..WS-010; TIME-API-001..003; UI-080; NTF-TIME; UAT-07.

**In scope of the approved Backend Core ticket.** Request, scoped list (pending, for the decider), approve and reject of a correction of the four supported timestamps; exact pause targeting for PAUSE_START/RESUME; chronology and not-in-the-future validation; Work Order lifecycle guard; atomic apply + audit; concurrency; regenerated migration. **Later tickets (not approved for this ticket):** Work Order timeline visibility, Angular request and review surfaces.

**Out of scope.** SLA recalculation, SLA risk/breach worker, SLA Override; notification foundation and NTF-TIME delivery; UC-REP-001, dashboards, PDF/print; Flutter; reopen of any Work Order or approval; any change to a Cost Summary amount; Visit rescheduling; bulk corrections; editing or withdrawing a pending correction; data backfill.

## 3. Evidence — what the rules and the code say today

| # | Subject | Class | Evidence |
|---|---|---|---|
| R1 | Supported timestamps are exactly CHECK_IN, PAUSE_START, RESUME, CHECK_OUT | BASELINE_RULE | `docs/01` (Time Correction rule); `docs/05` TC-003; `docs/06` TC-TIME-001 ("only 4 types") |
| R2 | Correction stores old value (immutable snapshot), new value (must preserve chronology), reason (non-blank, ≤ 1000) | BASELINE_RULE | `docs/05` TC-004..TC-006; `docs/02` ("Persist reason, actor, time … old/new"; "valid sequence") |
| R3 | Technician or Team Lead requests; Supervisor approves or rejects | BASELINE_RULE | `docs/01` (roles); `docs/03` ST-TC-001..003; `docs/09` TIME-API-001..003; `docs/10` UI-080 |
| R4 | Reject requires a decision reason; `decided_by`, `decided_at` are Supervisor / system time | BASELINE_RULE | `docs/05` TC-009..TC-011; `docs/03` ST-TC-003 ("Reason") |
| R5 | SLA is not recalculated; `sla_recalculated` is always false in the MVP | BASELINE_RULE | `docs/05` TC-012; `docs/02` ("Time Correction no SLA recalc; Override updates effective due only"); `docs/04` UC-TIME-001 postcondition "SLA unchanged" |
| R6 | A correction changes work-duration facts only; the business state is unchanged ("approved applies timestamp only") | BASELINE_RULE | `docs/01` (duration-facts rule); `docs/04` UC-TIME-001 ("State Flow … business state unchanged") |
| R7 | Approve/Reject carry If-Match; UI keeps a "correction state/token" | BASELINE_RULE | `docs/09` TIME-API-002/003; `docs/10` flow table |
| R8 | Approval is atomic with its side effects; every significant action is audited with reason | BASELINE_RULE | `docs/09` (Atomicity row names "Time Correction approval"); BR-04/BR-05 |
| R9 | NTF-TIME: recipient is the correction requester, "show approve/reject result" | BASELINE_RULE | `docs/02` notification matrix; `docs/06` TC-TIME-002 ("Decision audit + NTF-TIME") |
| R10 | Pause history is one row per pause; `work_session.pause_start_at` is cleared on Resume and `resume_at` holds only the latest resume | APPROVED_DEVIATION | `docs/13` §4.12 (every pause is its own row), §4.13 (pause_start_at cleared) and their "Not in this round: Time Correction of PAUSE_START / RESUME" |
| R11 | Pause rows: `CK_work_session_pause_period` (`resumed_at ≥ paused_at`), at most one open pause per session; **no chronology CHECK on `work_session`**; pause rows have **no RowVersion** (the session RowVersion governs) | CODE_FACT | `WorkSessionPauseConfiguration.cs:14-15,34-37`; `WorkSessionConfiguration.cs:22-40`; `WorkSessionPause.cs:34-46` |
| R12 | Check-out copies its instant to `service_visit.completed_at`; there is no stored duration anywhere | CODE_FACT | `WorkSessionService.cs:154-155`; `ServiceVisit.cs:217-227`; grep of `backend/src`, `frontend/src` |
| R13 | The Service Report is a **live read model** (fixed no-tracking queries at request time, nothing persisted); it reads session check-in/out, pause rows and `visit.completed_at` | CODE_FACT | `ServiceReportStore.cs:9-14,73-94,152-157`; `docs/13` §4.28; `docs/16` D1 |
| R14 | Cost Summary uses no duration or session time (`TotalAmount` is entered by the Team Lead); Work Summary and Customer Acceptance check statuses only; no table copies session times | CODE_FACT | `CostSummary.cs:34-44`; `WorkSummaryStore.cs:77-92`; agent grep of all session-time readers |
| R15 | SLA reads no session or visit time | CODE_FACT | `SlaLifecycle.cs:16-21,34-45`; `SlaRecord.cs:27-28`; grep of `Application/Sla`, `Infrastructure/Sla` |
| R16 | Close sets only the Work Order to CLOSED and stops the SLA ("operation locked"); there is no explicit "locked" guard in the Work Session services — the lock is implicit (CLOSED has no outgoing transition, sessions are already CHECKED_OUT) and is proven for 16 commands that **do not include Time Correction** | BASELINE_RULE + APPROVED_DEVIATION + CODE_FACT | `docs/02` (Close row: "WO CLOSED; SLA stop; operation locked"); `docs/13` §4.20 Q5; `WorkOrderCloseService.cs:15-17,45,50`; `WorkOrderCloseEndpointsTests.cs:440` |
| R17 | Close and Cancel check only the Work Order RowVersion and load no Work Session; Pause/Resume/Check-out check the Session RowVersion | CODE_FACT | `WorkOrderCloseService.cs:45`; `WorkOrderCancelService.cs:50,56-91`; `WorkSessionService.cs:52,95,140` |
| R18 | BR numbers for Time Correction differ between baseline documents (see §1) | UNRESOLVED | `docs/01` vs `docs/04`/`docs/06` |
| R19 | Timeline V1: `WORK_ORDER` route only; entity types WORK_ORDER, SERVICE_VISIT, WORK_SESSION, CORRECTIVE_ACTION and one REPAIR_REQUEST row; `details` is a per-action allowlist of scalar keys; **no raw `reason`, no old/new JSON**; "a future reason disclosure needs its own role-aware decision"; actor = `actorId` + kind | APPROVED_DEVIATION | `docs/15` D2, D4, D5, D7, §4.1; `AuditTimelineStore.cs:38-70,87-111`; `AuditTimelineDetails.cs:15-38`; `docs/16` lists further timeline entity types as out of scope |
| R20 | Disclosure: the Service Report copies no pause/reschedule/cancel/missed free-text reasons and no uploader identity | APPROVED_DEVIATION | `docs/16` disclosure decision; `docs/13` §4.28 |
| R21 | Separation-of-duties precedents: Cost Summary reviewer ≠ preparer, checked as an object-level 403 before concurrency/state; Repair Request decision ≠ own request; Corrective Action plan approval deliberately has none. **Time Correction: the baseline states none** | APPROVED_DEVIATION / UNRESOLVED | `docs/13` §4.19; `CostSummaryService.cs:157-163`; `docs/13` §4.21 (d) |
| R22 | Scope: Team Lead/Supervisor see Work Orders through `UserSiteScope` on the Repair Request's Site; Technician sees own sessions; **no site-scoped Work Session scope exists on `main`** | CODE_FACT | `DataScope.cs:137-161,188-212`; `AuthorizationPolicies.cs:219-281` (no TimeCorrection policy; catalog = 35) |
| R23 | Notification foundation is not implemented; NTF-* are deferred | APPROVED_DEVIATION | `docs/13` §1; `docs/12` §8 |
| R24 | Held branch behaviour (reference only): writes only the four `work_session` columns; never touches pause rows or `service_visit.completed_at`; PAUSE_START is possible only while a pause is open, RESUME only for the latest resume; no Work Order status guard; no `RequestedBy ≠ caller` check; several PENDING corrections per session allowed and a later approval overwrites an earlier one without comparing `old_value`; Approve/Reject audit rows carry no `workSessionId`; session RowVersion is the only concurrency control and Close/Cancel are not protected | CODE_FACT (branch) | `WorkSession.cs` (branch), `TimeCorrectionService.cs:55-149`, `TimeCorrectionStore.cs:66-96`, `TimeCorrectionConfiguration.cs:68-69`, `WorkOrderAudit.cs:42-45,192-253` (all `git show feature/s6-time-correction-pending-scope:<path>`) |

## 4. Decision table (all PROPOSED_DECISION / PENDING_OWNER_APPROVAL unless stated)

### Pause history (owner proposal A)

| ID | Current rule | Gap | Proposal | Impact | Owner status |
|---|---|---|---|---|---|
| P1 | R1, R10, R11: four types; pauses are rows; session columns are latest-event only | `TC-003` has no pause identifier; the branch corrects only transient columns, so an older pause cannot be corrected and `work_session_pause` drifts from `work_session` | A request of type PAUSE_START or RESUME **must name the exact pause** (`workSessionPauseId`); it is forbidden for CHECK_IN/CHECK_OUT. The pause must belong to the same tenant and session, and a RESUME target must be closed. Approval updates **that pause row** (`paused_at` or `resumed_at`) and mirrors to `work_session.pause_start_at` only when that pause is still open, and to `work_session.resume_at` only when it is the latest pause, so the latest-event columns stay consistent. `old_value` is snapshotted from the targeted record. No other pause is changed, no status changes. This adds one nullable column to `time_correction` (a technical addition to TC-001..TC-012, like the pause-row deviation of §4.12) | Service Report reads corrected rows with no change (R13); audit keeps before/after | APPROVED (13 Oct 2026, §1A items 1–2) |
| P2 | R2 "must preserve chronology"; `work_session` has no chronology CHECK (R11) | which intervals are validated; equality; bounds | Validate against **every affected interval of the session**, non-decreasing (≤): `check_in ≤` every `paused_at`; per pause `paused_at ≤ resumed_at`; consecutive pauses do not overlap (`previous.resumed_at ≤ next.paused_at`); every `resumed_at ≤ check_out` when set; an open pause starts at or after `check_in` and the previous resume. Checked at request (422) and **re-checked at approval against the current facts** (409 STATE_CONFLICT, per ST-TC-002 "Sequence valid") | Prevents corrections that would break `CK_work_session_pause_period` or the session order | APPROVED as implemented by §1A items 7 and 13 (future bound = server UTC now; no Visit window) |
| P2a | — | A bound against the server clock ("not in the future") and against the Visit's scheduled window | **Was UNRESOLVED; answered by the owner (not in the future, no Visit window).** No baseline or earlier decision defined either; Check-in takes the server clock and `docs/13` §4.11 lists no schedule-window guard. No rule is proposed; the owner must choose none, "not in the future", or a window | — | ANSWERED (13 Oct 2026): bound = server UTC now, equal allowed; **no** Visit-window validation |
| P3 | R12: Check-out copies the instant to `service_visit.completed_at`; the Service Report shows it | A CHECK_OUT correction that changes only `work_session.check_out_at` leaves two "completion" times | **Option A (recommended):** apply a CHECK_OUT correction to `work_session.check_out_at` **and** `service_visit.completed_at` in the same transaction (the same fact, stored twice). Option B: change only the session and accept the divergence | A keeps the report consistent; touches a Visit fact, so the owner must confirm it fits "applies timestamp only" | CONDITIONALLY APPROVED (§1A item 14): sync only when existing code proves the corrected Check-out is authoritative; never overwrite another session; otherwise report the gap |

### Downstream behaviour (owner proposal B)

| ID | Current rule | Gap | Proposal | Impact | Owner status |
|---|---|---|---|---|---|
| P4 | R16: Close/Cancel lock operations implicitly; Time Correction would not be blocked (R24) | no explicit lifecycle guard | **Request and Approve are refused with 409 STATE_CONFLICT when the Work Order is CLOSED or CANCELLED** (the baseline "operation locked" after Close; Cancel is terminal). Reject is proposed to stay allowed on any status because it changes no fact and lets a pending queue be cleared — owner to confirm | Extends the "16 commands" lock test to Time Correction | APPROVED (§1A items 3–4); Reject stays allowed after CLOSED/CANCELLED |
| P5 | R6, R13, R14: correction changes duration facts only; reports are live; no table stores session times; Cost Summary uses no duration | what happens to approvals already given | Within the allowed states a correction **never** reopens a Work Order, resets Work Summary review, Customer Acceptance or Cost Summary review, or changes any Cost Summary amount (Cost Summary does not depend on duration, R14). The Service Report simply shows the corrected facts when next read (live, R13); the earlier values stay in the audit rows. No new rule recomputes anything | No schema or code change outside Time Correction | APPROVED (§1A items 8–9) |
| P5a | `docs/04` UC-TIME-001 says only "traceable corrected work facts" | in which Work Order states a correction is allowed | **Owner chose A.** (A, recommended) any state before CLOSED/CANCELLED, which also covers AWAITING_CUSTOMER_ACCEPTANCE and COMPLETED; (B) only until the Customer accepts. The baseline does not decide; no default is proposed beyond naming the options | Determines the state guard of P4 | ANSWERED (13 Oct 2026): option A — every state before CLOSED/CANCELLED |

### Authorization, decisions and concurrency

| ID | Current rule | Gap | Proposal | Impact | Owner status |
|---|---|---|---|---|---|
| P6 | R3 (roles BASELINE_RULE); R22 (scope is CODE_FACT) | scope for Work Sessions is not defined for Team Lead/Supervisor | Technician: only own sessions. Team Lead (request) and Supervisor (decide, list): sessions of Work Orders whose Repair Request Site is in the caller's `UserSiteScope`, tenant-filtered; any miss, other tenant or other Site is the same non-leaking 404; other roles 403. New policies `TimeCorrection.Request` (TECHNICIAN, TEAM_LEAD) and `TimeCorrection.Decide` (SUPERVISOR); the policy catalog goes 35 → 37. New data-scope members for sessions/corrections (the branch ones are a starting point) | Backend authorization is authoritative; Angular guards are UX only | APPROVED (§1A item 12): Team Lead requests are site-scoped; policy catalog 35 → 37 |
| P7 | R21: precedents exist; baseline silent for Time Correction | self-decision | Deny Approve/Reject (403, checked before concurrency and state, as in Cost Summary review) when `requested_by` equals the caller, even for a user holding both TEAM_LEAD and SUPERVISOR | Aligns with the approved Cost Summary rule | APPROVED (§1A item 5) |
| P8 | R4, R7, R24 | stale and duplicate corrections | Check order 404 → 409 (If-Match against the correction's RowVersion) → 409 (state: only PENDING is decidable) → 422 (Reject reason). **At Approve the targeted current value must equal the stored `old_value`**, otherwise 409 (prevents the silent overwrite of R24). At most **one PENDING correction per (session, timestamp type, pause target)**: a duplicate request is 409. Approve and Reject never re-decide | Needs a filtered unique index; closes the sequential-overwrite gap | APPROVED (§1A items 6, 13); pending uniqueness enforced atomically by the database |
| P9 | R16, R17, R24: Close/Cancel check only the Work Order RowVersion | an Approve could interleave with Close/Cancel or with a concurrent Pause/Resume/Check-out | Approve runs in one transaction that **takes an exclusive lock on the Work Order row (or the repository's bounded transaction applock pattern, as used for routing and evidence) and re-reads the Work Order status**, loads the session and the targeted pause tracked with the session RowVersion as concurrency token, then applies, updates the correction and writes the audit rows. A lost race is 409; a lock not granted in time is 409 and writes nothing. Close/Cancel need no change | Two approvals on one session serialize; Close/Cancel cannot interleave | APPROVED (§1A item 13); locks are held until apply + audit + decision commit; both race orders must be proven over independent SQL connections |
| P10 | R5, R15 | — | Approval never touches SLA: no SLA code is referenced; `sla_recalculated` stays a constant false column (TC-012); an acceptance test proves the `sla_record` row (start, due, risk, `stopped_at`, stop reason) is unchanged | Preserves due/risk/stop values; no breach history exists yet to preserve beyond what R15 shows | BASELINE_RULE; APPROVED (§1A item 8) |

### Timeline and audit (owner proposal C)

| ID | Current rule | Gap | Proposal | Impact | Owner status |
|---|---|---|---|---|---|
| P11 | R19 | Time Correction rows are not in the Work Order timeline (the branch writes entity type `TIME_CORRECTION`, keyed on the correction id) | Show them in the existing `WORK_ORDER` timeline with the **existing scope** (`Audit.TimelineRead` roles + `IDataScope.WorkOrders`). Use the implementation's real names: entity type `TIME_CORRECTION`; action codes `TIME_CORRECTION_REQUESTED`, `TIME_CORRECTION_APPROVED`, `TIME_CORRECTION_REJECTED` (separate rows). Actor as in D5 (`actorId` + kind). `details` allowlist (scalars only): `workSessionId`, `workSessionPauseId` (when present), `timestampType`, `oldValue`, `newValue`. **All three audit rows must carry `workSessionId`** (the branch's Approve/Reject rows omit it). Files to change: `WorkOrderAudit.cs`, `AuditTimelineContracts.cs` (`TimeCorrectionIds`), `AuditTimelineStore.cs` (resolve correction ids by the session ids already derived), `AuditTimelineDetails.cs`; text changes in `docs/15` D2/D4/D7, `docs/13` §4.26 and the out-of-scope line of `docs/16` | The timeline route, cursor and controller are unchanged | APPROVED IN PRINCIPLE (§1A item 11); implementation is a **later ticket** |
| P11a | R19: "no raw reason; reason disclosure needs its own role-aware decision" | the request asks to show the reason | The **reason stays in the append-only audit rows** (always stored: BR-05, TC-006, TC-010) and is **not** added to the timeline by this proposal. Showing it (for example to Team Lead and Supervisor only) is a separate role-aware decision the owner must take | Keeps D4 and the Service Report disclosure decision intact | ANSWERED (13 Oct 2026): the raw reason is omitted from the timeline |

### Notification (owner proposal D)

| ID | Current rule | Gap | Proposal | Impact | Owner status |
|---|---|---|---|---|---|
| P12 | R9, R23 | the baseline expects NTF-TIME; no notification foundation exists | **Defer NTF-TIME** to the notification foundation. **Deviation recorded:** UC-TIME-001 "notify requester" and TC-TIME-002 "NTF-TIME" are not met by this ticket. **Follow-up:** when the foundation exists, emit NTF-TIME to the correction requester with the decision result. Until then the requester sees the result through the correction's own status (GET/list); nothing is sent and no document may claim a notification was sent | Same treatment as every other NTF-* | APPROVED (§1A item 10) — now an APPROVED_DEVIATION: NTF-TIME is deferred, nothing is sent |

### Data, migration and surfaces

| ID | Current rule | Proposal | Owner status |
|---|---|---|---|
| P13 | R24: the branch migration `20260923132329_AddTimeCorrection` and its snapshot predate Sprints 3–5 | **Regenerate** the migration from the latest model snapshot, with an id after `20261011162724_AddWorkEvidence`; additive, schema only, no backfill; the old Designer and snapshot are never reused. Columns per TC-001..TC-012 plus nullable `work_session_pause_id` (P1) with a tenant-consistent composite foreign key; CHECK constraints on type, status and non-blank reasons; the unique filtered index of P8 | APPROVED (§1A item 13): regenerate from the current snapshot; never reuse the old migration, Designer or snapshot |
| P14 | `docs/10` UI-080: Tech/Team Lead/Supervisor request and decide | Technician request: the existing active Work Session panel (as the branch does). Supervisor review: a route guarded by the existing `supervisorOnlyGuard`, following `cost-summary-review`. **Team Lead request surface is UNRESOLVED:** no existing Team Lead screen shows sessions; candidates are the Service Report page (it already lists sessions and pauses) or the Work Order detail | Technician and Supervisor Angular surfaces: **later ticket**; Team Lead surface: UNRESOLVED (later ticket) |

## 5. Acceptance criteria (for the implementation, after approval)

1. **Authorization, scope, ownership.** 401 anonymous; 403 for every role outside the policy; Technician requests only for own sessions; Team Lead only on Sites in their `UserSiteScope`; only the Supervisor decides, within `UserSiteScope`; other tenant, other Site, other Technician, unknown id → the same 404. A forged `tenantId`/`requestedBy`/`status`/`oldValue`/`decidedBy` in the body has no effect (the contract has no such members).
2. **Self-decision (if P7 is approved).** A user holding TEAM_LEAD and SUPERVISOR who requested a correction gets 403 on Approve and Reject, before any state or concurrency check; a different Supervisor succeeds.
3. **Validation.** Only the four types; `workSessionPauseId` required for PAUSE_START/RESUME and rejected for the others; wrong-session or other-tenant pause → 422/404 without disclosure; `newValue` needs an explicit UTC offset; reason non-blank ≤ 1000; Reject requires a non-blank decision reason; a value equal to the current one is refused with `422` on `newValue`, writing no correction and no audit row (**owner-approved 14 October 2026**; P2 chronology applies).
4. **Chronology.** The P2 rules are enforced at request (422) and re-validated at approval against current facts (409); corrections that would break `CK_work_session_pause_period`, pause order or check-in/out order are refused.
5. **If-Match and stale approval.** Approve/Reject without If-Match → 400; stale If-Match → 409 CONCURRENCY_CONFLICT; a second decision → 409; an Approve whose target changed since the request (`old_value` mismatch) → 409 and nothing is written.
6. **Atomic apply + audit.** The pause row / session columns / (CHECK_OUT) `service_visit.completed_at`, the correction status and the audit rows commit together or not at all; an injected failure after the apply rolls everything back.
7. **Concurrency.** Two approvals on one session serialize (exactly one applies the same target); an Approve racing a Pause/Resume/Check-out of the session yields 409 or a consistent result, never a torn state; an Approve racing Close or Cancel never applies to a CLOSED/CANCELLED Work Order (P4, P9); duplicate pending requests → 409 (P8).
8. **Lifecycle guard.** Request and Approve on CLOSED or CANCELLED → 409 and no row changes; the owner's choice for P5a is enforced exactly.
9. **SLA preserved.** The `sla_record` row (start, original/effective due, risk, `stopped_at`, stop reason) and the repository's SLA read result are identical before and after Approve, including after the Work Order was Closed earlier; no SLA type is referenced by Time Correction code.
10. **Downstream untouched.** Work Summary, Customer Acceptance, Cost Summary rows and Work Order status/RowVersion do not change on Approve; the Service Report (live) shows the corrected check-in/out and pause periods; the Cost section is unchanged; no free-text reason appears in the report.
11. **Timeline and audit (if P11 is approved).** `TIME_CORRECTION_REQUESTED/APPROVED/REJECTED` appear as separate timeline rows for the Work Order within the existing scope, with actor and the allowlisted before/after; no reason, no raw JSON; nothing from another Work Order, tenant or Site; the cursor and ordering behave as before; every row has `workSessionId`.
12. **Migration.** Schema-only, additive, applied on top of the latest snapshot; `has-pending-model-changes` is clean; script inspection shows no data statement except the history row; policy catalog count 37.
13. **Test infrastructure.** API tests derive from `AuthApiFactory` and so keep the PR #26 `LocalDbLifecycleGate`; the API suite passes three consecutive runs with no retries inside tests.
14. **Angular.** Request dialog (Technician) and review screen (Supervisor): loading/empty/error/forbidden states, no duplicate submit while in flight, a stale-token message on 409, `supervisorOnlyGuard` as UX only.

## 6. Regression matrix

| Area | Level | What must be asserted |
|---|---|---|
| Domain | unit | type/status transitions; chronology evaluator for P2 over multi-pause sessions; `WorkSession` apply keeps `Status`; pause mirror rules of P1 |
| Application | unit with fakes | check order 404→409→409→422; self-decision order; stale `old_value`; terminal-state guard; audit rows complete (all carry `workSessionId`) |
| API | SQL Server host | full actor/scope/tenant matrix; If-Match; two-decide race; Approve vs Close/Cancel race; Approve vs Pause/Resume/Check-out; duplicate pending; tampered body fields; Service Report live check; timeline rows; SLA unchanged; **Time Correction added to the `AfterClose_EveryMutationCommand_IsRejected…` lock test** |
| Integration | SQL Server | schema (columns, CHECKs, composite FKs, filtered unique index); migration on a populated database creates no row and changes none; atomic rollback with an injected audit failure; a failure injected just before the commit rolls everything back and is not turned into a `409`; a deadlock victim maps to `409` and writes nothing (a lock wait beyond the command timeout is a server error, nothing written) |
| Existing behaviour | API/Application | Pause, Resume, Check-out, Work Summary, Acceptance, Cost Summary, Close, Cancel, Service Report cost redaction, evidence guard and SLA tests unchanged and green; authorization policy count and role map updated deliberately (35 → 37) |
| Angular | vitest | request dialog and review screen behaviours of AC-14; route guards |
| Tooling | CI | build with only the existing xUnit2029 warning; API suite × 3 on LocalDB |

## 7. Selective-port plan (nothing is executed by this sheet)

1. After owner approval, record the answers in §10 and update `docs/13` (a **new** section, not the branch's §4.15, which collides with the Sprint 4 section of the same number), `docs/15` (timeline entity types and payload), `docs/12` §8 and this sheet's status.
2. Create a new branch from clean `main`; leave `feature/s6-time-correction-pending-scope` untouched as a reference.
3. **Reuse as written** (independent files, unchanged on `main` since the merge-base for the domain parts): `TimeCorrection`, status, transitions and timestamp-type domain files; `TimeCorrectionConfiguration`; domain unit tests; Angular service/models/review component.
4. **Adapt:** `WorkSession` (pause-aware apply and chronology), `TimeCorrectionService`/store (P1–P9), `WorkOrderAudit`, `AuthorizationPolicies` (+2), `IDataScope`/`DataScope` (new members), DI registrations, API controllers/contracts, `app.routes.ts`, the active Work Session component (changed on both sides), `AuthorizationPoliciesTests` (35 → 37), the timeline store/details/contracts (P11).
5. **Do not reuse:** the old migration, Designer and model snapshot; the branch's `docs/13` edit; the add/add `supervisor-only.guard` (use the one already on `main`).
6. **Regenerate the migration** from the latest snapshot (P13) using a build that includes the new model; verify with `has-pending-model-changes` and a migration SQL inspection.
7. Open the implementation as small reviewable changes: backend core first (domain, schema, application, API, audit, tests), then timeline visibility, then Angular.

A merge, rebase or single-commit cherry-pick is rejected because the migration and snapshot are obsolete, 15 files changed on both sides and a clean textual merge would not prove semantic compatibility.

## 8. First implementation ticket (approved 13 October 2026: Backend Core)

**Time Correction backend core:** request, list, approve and reject for the four types with exact pause targeting (P1–P3), chronology (P2), lifecycle guard and concurrency (P4, P8, P9), SLA preservation (P10), audit with `workSessionId` on every row, the regenerated migration (P13), policies/scope (P6, P7) and the full backend regression of §6. Exclusions: timeline visibility (P11), Angular, NTF-TIME, anything in §2 "Out of scope". It is the smallest slice that delivers the baseline behaviour with SLA and downstream data provably untouched; the timeline and Angular slices depend only on its audit rows and API.

## 9. Documents to update on approval (done / still to do)

`docs/13` (new Time Correction section §4.29, §1 and §6 rows, policy table — updated with the Backend Core change), `docs/12` §8 (pointer row updated; NTF-TIME deviation recorded), this sheet (status, §10). Still to do in later tickets: `docs/15` (D2/D4/D7 and §4.1 for the timeline slice, P11) and the Angular parts of `docs/13`.

## 10. Questions for the owner and the answers of 13 October 2026

| # | Question | Related | Owner answer (13 October 2026) |
|---|---|---|---|
| Q1 | Approve exact pause targeting with a `workSessionPauseId` on PAUSE_START/RESUME corrections? | P1 | Approved: exact pause targeting with `workSessionPauseId`. |
| Q2 | Should a CHECK_OUT correction also update `service_visit.completed_at` (Option A) or only the session (Option B)? | P3 | Conditional (§1A item 14): sync `completed_at` only when existing code proves the corrected Check-out is its authoritative source; never overwrite another session; report any ambiguity. |
| Q3 | Any bound on the new value: none, "not in the future", or the Visit window? | P2a | Bound = server UTC now (the new value must not exceed it); no Visit-window validation. |
| Q4 | Which Work Order states allow a correction: before CLOSED/CANCELLED (A) or only until the Customer accepts (B)? May Reject be used on CLOSED/CANCELLED? | P4, P5a | Request/Approve allowed in every state except CLOSED/CANCELLED; Reject stays allowed after CLOSED/CANCELLED and changes no time fact. |
| Q5 | Add the self-decision denial and the one-pending-per-target rule? | P7, P8 | Approved: no self-decision; one pending per session + correction type + pause id where applicable. |
| Q6 | Show Time Correction events in the Work Order timeline with before/after but without the reason? Or disclose the reason role-aware (to whom)? | P11, P11a | Existing scope, actor and allowlisted before/after; no raw reason. Implementation in a later ticket. |
| Q7 | Defer NTF-TIME to the notification foundation and record it as an approved deviation? | P12 | Approved: NTF-TIME deferred; nothing is sent. |
| Q8 | Where does a Team Lead request a correction (Service Report page, Work Order detail, or another screen)? | P14 | Team Lead requests stay site-scoped; the Angular entry point (including the Team Lead surface) follows in a later ticket — still UNRESOLVED. |
| Q9 | Confirm the BR numbering inconsistency between baseline documents is documentation-only. | R18 | Not answered — remains UNRESOLVED (documentation-only inconsistency, R18). |

## 11. Traceability

FR-06, FR-08; BR-04, BR-05; Time Correction rules of `docs/01`/`docs/02`; UC-TIME-001; ST-TC-001..003; TC-TIME-001/002; TC-001..TC-012; WS-006..WS-010; TIME-API-001..003; UI-080; NTF-TIME; UAT-07; `docs/13` §4.12–§4.14, §4.19, §4.20, §4.26, §4.27, §4.28; `docs/15` D2/D4/D5/D7; `docs/16` disclosure decision and exclusions.
