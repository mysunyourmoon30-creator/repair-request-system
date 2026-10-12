import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ServiceReportResponse } from './service-report.models';

/**
 * SVR-API-001 and WEV-API-002/003 client (`docs/13` §4.28). Read-only: the Service Report has no write path, and evidence content is only
 * ever fetched through the authorized download endpoint (never a storage URL). Role, Site scope, the exact Acceptance Contact rule and the
 * CLEAN-only rule are enforced by the backend on every request; nothing here is a security boundary.
 */
@Injectable({ providedIn: 'root' })
export class ServiceReportService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

  getServiceReport(workOrderId: string): Observable<ServiceReportResponse> {
    return this.http.get<ServiceReportResponse>(`${this.baseUrl}/${workOrderId}/service-report`);
  }

  /** WEV-API-003: the file content as a Blob; the server only serves CLEAN evidence the caller may open. */
  downloadEvidence(workOrderId: string, evidenceId: string): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/${workOrderId}/evidence/${evidenceId}/content`, { responseType: 'blob' });
  }
}
