/**
 * Repair Request detail shape (S2-002). Matches `RepairRequestDraftResponse` on the backend exactly — only the
 * fields the Convert screen needs (status gate, Request No., description) are rendered.
 */
export interface RepairRequest {
  id: string;
  status: string;
  requestNo: string | null;
  siteId: string | null;
  equipmentId: string | null;
  requestCategoryCode: string | null;
  priorityCode: string | null;
  requestContactId: string | null;
  description: string | null;
  preferredStartAt: string | null;
  preferredEndAt: string | null;
  createdBy: string;
  submittedAt: string | null;
  rowVersion: string;
}

export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/** Canonical Repair Request status codes (RR-DD-001 RR-004), for the list's status filter. */
export const REPAIR_REQUEST_STATUSES: readonly string[] = [
  'DRAFT',
  'SUBMITTED',
  'UNDER_REVIEW',
  'APPROVED',
  'REJECTED',
  'CANCELLED',
  'CONVERTED',
];
