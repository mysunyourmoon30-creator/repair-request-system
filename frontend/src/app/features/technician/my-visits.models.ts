/**
 * "My Visits" list item (S3-001; UI-040). Matches `MyVisitSummaryResponse` on the backend exactly — a
 * lightweight projection, not the full Work Order graph the Coordinator-facing detail page uses.
 */
export interface MyVisitSummary {
  serviceVisitId: string;
  workOrderId: string;
  workOrderNo: string;
  status: string;
  siteCode: string | null;
  equipmentCode: string | null;
  scheduledStartAt: string | null;
  scheduledEndAt: string | null;
  rowVersion: string;
}

export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}
