import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, of, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { WorkOrder } from './work-order.models';
import { WorkSummary } from './work-summary.models';

export interface SubmitWorkSummaryRequest {
  summaryText: string;
  repairOutcomeCode: string;
}

/**
 * Calls the Work Summary Submit/Review endpoints (`docs/13` §4.15): `POST /api/v1/work-orders/{id}/submit-work-summary`
 * (WSM-API-001, Technician), `POST /api/v1/work-orders/{id}/submit-for-acceptance` (WSM-API-002, Team Lead/
 * Supervisor) and `GET /api/v1/work-orders/{id}/work-summary`. Both writes return the parent Work Order, the
 * same convention every other Work Order/Work Session action uses.
 */
@Injectable({ providedIn: 'root' })
export class WorkSummaryService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

  submit(workOrderId: string, ifMatch: string, body: SubmitWorkSummaryRequest): Observable<WorkOrder> {
    return this.http.post<WorkOrder>(`${this.baseUrl}/${workOrderId}/submit-work-summary`, body, { headers: { 'If-Match': ifMatch } });
  }

  submitForAcceptance(workOrderId: string, ifMatch: string): Observable<WorkOrder> {
    return this.http.post<WorkOrder>(`${this.baseUrl}/${workOrderId}/submit-for-acceptance`, null, { headers: { 'If-Match': ifMatch } });
  }

  /** 404 (not submitted yet, or out of scope) is treated as `null`, not an error — the caller decides what to show. */
  get(workOrderId: string): Observable<WorkSummary | null> {
    return this.http.get<WorkSummary>(`${this.baseUrl}/${workOrderId}/work-summary`).pipe(
      catchError((response: HttpErrorResponse) => (response.status === 404 ? of(null) : throwError(() => response))),
    );
  }
}
