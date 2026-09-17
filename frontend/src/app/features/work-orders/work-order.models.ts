/**
 * Work Order list/detail shape (S2-001). Matches `WorkOrderResponse` on the backend exactly.
 * Scheduled Date and Assigned Team/Technician are intentionally absent (DEC-S2-001-02/03,
 * `docs/12` Section 11) — Customer/Site/Equipment are codes, not display names.
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
}

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
