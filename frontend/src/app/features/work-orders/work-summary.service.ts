import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, of, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { WorkOrder } from './work-order.models';
import { EligibleAcceptanceContact, WorkSummary } from './work-summary.models';

export interface SubmitWorkSummaryRequest {
  summaryText: string;
  repairOutcomeCode: string;
}

/**
 * Calls the Work Summary Submit/Review endpoints (`docs/13` §4.15/§4.16): `POST /api/v1/work-orders/{id}/submit-work-summary`
 * (WSM-API-001, Technician), `POST /api/v1/work-orders/{id}/submit-for-acceptance` (WSM-API-002, Team Lead/
 * Supervisor — now carrying `acceptanceContactId`), `GET /api/v1/work-orders/{id}/work-summary` and
 * `GET /api/v1/work-orders/{id}/eligible-acceptance-contacts` (ACC-API-ADD-001). Both writes return the parent
 * Work Order, the same convention every other Work Order/Work Session action uses.
 */
@Injectable({ providedIn: 'root' })
export class WorkSummaryService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

  submit(workOrderId: string, ifMatch: string, body: SubmitWorkSummaryRequest): Observable<WorkOrder> {
    return this.http.post<WorkOrder>(`${this.baseUrl}/${workOrderId}/submit-work-summary`, body, { headers: { 'If-Match': ifMatch } });
  }

  /** `acceptanceContactId` must come from {@link getEligibleAcceptanceContacts} — never a free-text id (`docs/13` §4.16 Decision 3). */
  submitForAcceptance(workOrderId: string, ifMatch: string, acceptanceContactId: string): Observable<WorkOrder> {
    return this.http.post<WorkOrder>(`${this.baseUrl}/${workOrderId}/submit-for-acceptance`, { acceptanceContactId }, { headers: { 'If-Match': ifMatch } });
  }

  /** ACC-API-ADD-001: every REQUESTER eligible to be designated as this Work Order's Acceptance Contact. */
  getEligibleAcceptanceContacts(workOrderId: string): Observable<EligibleAcceptanceContact[]> {
    return this.http.get<EligibleAcceptanceContact[]>(`${this.baseUrl}/${workOrderId}/eligible-acceptance-contacts`);
  }

  /** 404 (not submitted yet, or out of scope) is treated as `null`, not an error — the caller decides what to show. */
  get(workOrderId: string): Observable<WorkSummary | null> {
    return this.http.get<WorkSummary>(`${this.baseUrl}/${workOrderId}/work-summary`).pipe(
      catchError((response: HttpErrorResponse) => (response.status === 404 ? of(null) : throwError(() => response))),
    );
  }
}
