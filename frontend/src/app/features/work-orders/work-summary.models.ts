/**
 * Work Summary shape (UC-WO-020; `docs/13` §4.15). Matches `WorkSummaryResponse` on the backend exactly.
 * `revisionNo` is always 1 in this ticket's scope — no resubmission/revise cycle exists yet.
 */
export interface WorkSummary {
  workSummaryId: string;
  workOrderId: string;
  serviceVisitId: string;
  revisionNo: number;
  summaryText: string;
  repairOutcomeCode: string;
}

/**
 * Closed six-value allowlist for `repairOutcomeCode` (WSM-007; `docs/13` §4.15 Decision 5 — Portfolio Project
 * Owner directive). Mirrors the backend `RepairOutcomeCode` enum exactly; adding/removing a value here without
 * a matching backend change would desync the dropdown from what the server actually accepts. The submit form
 * offers only these as a `<select>` — never a free-text field — since the backend rejects anything else.
 */
export const REPAIR_OUTCOME_CODES: ReadonlyArray<{ code: string; label: string }> = [
  { code: 'REPAIRED', label: 'Repaired — completed successfully, equipment back in service' },
  { code: 'TEMPORARY_FIX', label: 'Temporary Fix — usable but further action required' },
  { code: 'PARTS_REQUIRED', label: 'Parts Required — cannot continue until parts arrive' },
  { code: 'NO_FAULT_FOUND', label: 'No Fault Found — fault could not be found or reproduced' },
  { code: 'NOT_REPAIRABLE', label: 'Not Repairable — equipment cannot be repaired' },
  { code: 'FOLLOW_UP_REQUIRED', label: 'Follow-Up Required — a further appointment or inspection is needed' },
];
