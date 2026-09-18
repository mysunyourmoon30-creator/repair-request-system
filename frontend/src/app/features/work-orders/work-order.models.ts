/**
 * Work Order list/detail shape (S2-001; extended S2-003 with `visits`). Matches `WorkOrderResponse` on the
 * backend exactly. Customer/Site/Equipment are codes, not display names.
 */
export interface WorkOrder {
  workOrderId: string;
  workOrderNo: string;
  status: string;
  repairRequestId: string;
  repairRequestNo: string | null;
  customerCode: string | null;
  siteCode: string | null;
  equipmentCode: string | null;
  rowVersion: string;
  visits: ServiceVisit[];
}

/**
 * Service Visit shape (S2-003). Matches `ServiceVisitResponse` on the backend exactly. Team/technician are raw
 * ids — there is no directory to resolve a display name from (no Team master data exists at all).
 */
export interface ServiceVisit {
  serviceVisitId: string;
  workOrderId: string;
  visitType: string;
  status: string;
  assignedTeamId: string | null;
  assignedTechnicianId: string | null;
  scheduledStartAt: string | null;
  scheduledEndAt: string | null;
  rescheduleReason: string | null;
  reassignReason: string | null;
  cancelReason: string | null;
  missedReason: string | null;
  completedAt: string | null;
  sourceMissedVisitId: string | null;
  missedDecisionCode: string | null;
  missedDecidedAt: string | null;
  rowVersion: string;
}

/** Canonical Missed Decision codes (RR-DD-001 SV-017; D-15), for the Decide Missed form. */
export const MISSED_VISIT_DECISIONS: readonly string[] = ['RESCHEDULE', 'FOLLOW_UP', 'REASSIGN', 'NO_FOLLOW_UP'];

export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/** Canonical Work Order status codes (RR-DD-001 WO-005; RR-STS-001 section 3), for the list's status filter. */
export const WORK_ORDER_STATUSES: readonly string[] = [
  'OPEN',
  'SCHEDULED',
  'IN_PROGRESS',
  'AWAITING_SUPERVISOR_REVIEW',
  'AWAITING_CUSTOMER_ACCEPTANCE',
  'COMPLETED',
  'CORRECTIVE_ACTION_REQUIRED',
  'CORRECTIVE_PLAN_PENDING',
  'CORRECTIVE_PLAN_APPROVED',
  'CLOSED',
  'CANCELLED',
];
