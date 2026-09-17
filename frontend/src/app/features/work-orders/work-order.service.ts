import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PagedResponse, WorkOrder } from './work-order.models';

/** Calls the S2-001 `GET /api/v1/work-orders` and `GET /api/v1/work-orders/{id}` endpoints. */
@Injectable({ providedIn: 'root' })
export class WorkOrderService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

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
}
