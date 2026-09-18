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
