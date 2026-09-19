import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { MyVisitSummary, PagedResponse } from './my-visits.models';

/**
 * Calls S3-001's Technician-only endpoints: `GET /api/v1/service-visits/mine` (the "My Visits" list) and
 * `POST /api/v1/service-visits/{id}/check-in` (WS-API-001). Check-in returns the parent Work Order (the same
 * `WorkOrderResponse` shape the Coordinator flows use), not another `MyVisitSummary` — the caller reloads the
 * list afterward rather than trying to patch one row in place.
 */
@Injectable({ providedIn: 'root' })
export class MyVisitsService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/v1/service-visits`;

  list(page: number, pageSize: number): Observable<PagedResponse<MyVisitSummary>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResponse<MyVisitSummary>>(`${this.baseUrl}/mine`, { params });
  }

  checkIn(serviceVisitId: string, ifMatch: string): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/${serviceVisitId}/check-in`, null, { headers: { 'If-Match': ifMatch } });
  }
}
