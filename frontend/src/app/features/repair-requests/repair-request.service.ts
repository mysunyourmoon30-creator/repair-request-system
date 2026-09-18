import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../../environments/environment';
import { WorkOrder } from '../work-orders/work-order.models';
import { PagedResponse, RepairRequest } from './repair-request.models';

export interface RepairRequestWithETag {
  repairRequest: RepairRequest;
  eTag: string | null;
}

/**
 * Calls `GET /api/v1/repair-requests`, `GET /api/v1/repair-requests/{id}` and RR-API-010's
 * `POST /api/v1/repair-requests/{id}/convert-to-work-order` (S2-002). `get` reads the response's `ETag` header
 * so the caller can send it back as `If-Match` on Convert.
 */
@Injectable({ providedIn: 'root' })
export class RepairRequestService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/v1/repair-requests`;

  list(page: number, pageSize: number, status: string | null): Observable<PagedResponse<RepairRequest>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (status) {
      params = params.set('status', status);
    }

    return this.http.get<PagedResponse<RepairRequest>>(this.baseUrl, { params });
  }

  get(repairRequestId: string): Observable<RepairRequestWithETag> {
    return this.http
      .get<RepairRequest>(`${this.baseUrl}/${repairRequestId}`, { observe: 'response' })
      .pipe(map((response) => ({ repairRequest: response.body!, eTag: response.headers.get('ETag') })));
  }

  convert(repairRequestId: string, ifMatch: string): Observable<WorkOrder> {
    return this.http.post<WorkOrder>(`${this.baseUrl}/${repairRequestId}/convert-to-work-order`, null, {
      headers: { 'If-Match': ifMatch },
    });
  }
}
