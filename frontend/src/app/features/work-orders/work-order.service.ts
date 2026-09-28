import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PagedResponse, WorkOrder } from './work-order.models';

export interface ScheduleWorkOrderRequest {
  ownerTeamId: string;
  assignedTechnicianId: string;
  scheduledStartAt: string;
  scheduledEndAt: string;
}

export interface RescheduleServiceVisitRequest {
  reason: string;
  scheduledStartAt: string;
  scheduledEndAt: string;
}

export interface ReassignServiceVisitRequest {
  reason: string;
  assignedTeamId: string;
  assignedTechnicianId: string;
}

export interface ReasonOnlyRequest {
  reason: string;
}

/** ACC-API-002 Reject body (`docs/13` §4.17). Required — the exact field name is `decisionReason`, not `reason`. */
export interface RejectWorkOrderRequest {
  decisionReason: string;
}

/** CST-API-001 Prepare body (`docs/13` §4.18). `totalAmount`/`currencyCode` required; `note` optional. */
export interface PrepareCostSummaryRequest {
  totalAmount: number;
  currencyCode: string;
  note: string | null;
}

/**
 * Cost Summary shape (`docs/13` §4.18). Matches `CostSummaryResponse` on the backend exactly. `reviewedBy`/
 * `reviewedAt` are always null in this ticket's scope (Prepare only never sets them).
 */
export interface CostSummary {
  costSummaryId: string;
  workOrderId: string;
  totalAmount: number;
  currencyCode: string;
  note: string | null;
  preparedBy: string;
  preparedAt: string;
  reviewedBy: string | null;
  reviewedAt: string | null;
  rowVersion: string;
}

/** One queue item (`docs/13` §4.19) — a lightweight projection, never the full Work Order graph. */
export interface PendingCostSummaryReview {
  workOrderId: string;
  workOrderNo: string;
  siteCode: string | null;
  totalAmount: number;
  currencyCode: string;
  preparedBy: string;
  preparedAt: string;
}

/** Note: field names differ from `ScheduleWorkOrderRequest` (`assignedTeamId`, not `ownerTeamId`) — matches the backend's `NewVisitScheduleRequest`. */
export interface NewVisitScheduleRequest {
  assignedTeamId: string;
  assignedTechnicianId: string;
  scheduledStartAt: string;
  scheduledEndAt: string;
}

export interface MissedDecisionRequest {
  decision: string;
  reason: string;
  newSchedule: NewVisitScheduleRequest | null;
}

/**
 * Calls the S2-001 Work Order List/Detail endpoints, S2-003's `POST /api/v1/work-orders/{id}/schedule`
 * (WO-API-002) and the Service Visit action endpoints (`/api/v1/service-visits/{id}/...`, WO-API-003..007). Every
 * write returns the parent Work Order (with its full `visits` array), so the caller never needs a separate fetch.
 */
@Injectable({ providedIn: 'root' })
export class WorkOrderService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;
  private readonly visitsBaseUrl = `${environment.apiBaseUrl}/v1/service-visits`;

  list(page: number, pageSize: number, status: string | null): Observable<PagedResponse<WorkOrder>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (status) {
      params = params.set('status', status);
    }

    return this.http.get<PagedResponse<WorkOrder>>(this.baseUrl, { params });
  }

  get(workOrderId: string): Observable<WorkOrder> {
    return this.http.get<WorkOrder>(`${this.baseUrl}/${workOrderId}`);
  }

  schedule(workOrderId: string, ifMatch: string, body: ScheduleWorkOrderRequest): Observable<WorkOrder> {
    return this.http.post<WorkOrder>(`${this.baseUrl}/${workOrderId}/schedule`, body, { headers: { 'If-Match': ifMatch } });
  }

  /** ACC-API-001 Customer Accept (ST-WO-005; `docs/13` §4.16). No body — the caller's identity is resource-specific, re-checked server-side. */
  accept(workOrderId: string, ifMatch: string): Observable<WorkOrder> {
    return this.http.post<WorkOrder>(`${this.baseUrl}/${workOrderId}/accept`, null, { headers: { 'If-Match': ifMatch } });
  }

  /** ACC-API-002 Customer Reject (ST-WO-007; `docs/13` §4.17). Same caller as Accept; `decisionReason` is required. */
  reject(workOrderId: string, ifMatch: string, body: RejectWorkOrderRequest): Observable<WorkOrder> {
    return this.http.post<WorkOrder>(`${this.baseUrl}/${workOrderId}/reject`, body, { headers: { 'If-Match': ifMatch } });
  }

  /**
   * CST-API-001 Cost Summary Prepare (Team Lead only; `docs/13` §4.18). `ifMatch` is the Work Order's own
   * rowVersion on first Prepare (no Cost Summary exists yet), or the Cost Summary's own `rowVersion` (from the
   * previous Prepare response, or `workOrder.costSummaryRowVersion`) on a later edit — never the Work Order's own
   * token once a Cost Summary already exists, since Prepare never changes the Work Order's own status.
   */
  prepareCostSummary(workOrderId: string, ifMatch: string, body: PrepareCostSummaryRequest): Observable<CostSummary> {
    return this.http.put<CostSummary>(`${this.baseUrl}/${workOrderId}/cost-summary`, body, { headers: { 'If-Match': ifMatch } });
  }

  /**
   * Cost Summary read (`docs/13` §4.19, technical addition). Team Lead (their own Prepare scope) or Supervisor
   * (site-wide, to review). 404 when none has been prepared yet, or the caller is out of scope.
   */
  getCostSummary(workOrderId: string): Observable<CostSummary> {
    return this.http.get<CostSummary>(`${this.baseUrl}/${workOrderId}/cost-summary`);
  }

  /**
   * The Supervisor pending Cost Summary Review queue (`docs/13` §4.19, technical addition): every Work Order,
   * within the caller's Site scope, that is COMPLETED with an unreviewed Cost Summary.
   */
  listPendingCostSummaryReviews(page: number, pageSize: number): Observable<PagedResponse<PendingCostSummaryReview>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResponse<PendingCostSummaryReview>>(`${this.baseUrl}/pending-cost-summary-review`, { params });
  }

  /**
   * CST-API-002 Review Cost Summary (Supervisor only; `docs/13` §4.19). No body — `reviewedBy`/`reviewedAt` are
   * always server-derived. `ifMatch` is the Cost Summary's own `rowVersion`.
   */
  reviewCostSummary(workOrderId: string, ifMatch: string): Observable<CostSummary> {
    return this.http.post<CostSummary>(`${this.baseUrl}/${workOrderId}/review-cost-summary`, null, { headers: { 'If-Match': ifMatch } });
  }

  reschedule(serviceVisitId: string, ifMatch: string, body: RescheduleServiceVisitRequest): Observable<WorkOrder> {
    return this.post(serviceVisitId, 'reschedule', ifMatch, body);
  }

  reassign(serviceVisitId: string, ifMatch: string, body: ReassignServiceVisitRequest): Observable<WorkOrder> {
    return this.post(serviceVisitId, 'reassign', ifMatch, body);
  }

  cancelVisit(serviceVisitId: string, ifMatch: string, body: ReasonOnlyRequest): Observable<WorkOrder> {
    return this.post(serviceVisitId, 'cancel', ifMatch, body);
  }

  markMissed(serviceVisitId: string, ifMatch: string, body: ReasonOnlyRequest): Observable<WorkOrder> {
    return this.post(serviceVisitId, 'mark-missed', ifMatch, body);
  }

  decideMissed(serviceVisitId: string, ifMatch: string, body: MissedDecisionRequest): Observable<WorkOrder> {
    return this.post(serviceVisitId, 'missed-decision', ifMatch, body);
  }

  private post(serviceVisitId: string, action: string, ifMatch: string, body: unknown): Observable<WorkOrder> {
    return this.http.post<WorkOrder>(`${this.visitsBaseUrl}/${serviceVisitId}/${action}`, body, { headers: { 'If-Match': ifMatch } });
  }
}
