import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { EvidenceType, WorkEvidenceItem, WorkEvidenceList } from './work-evidence.models';

/**
 * WEV-API-001/002 client (`docs/13` §4.28). Upload sends only the file and the closed evidence type — never a tenant, uploader, Visit,
 * scan status or binding id; the server derives all of them. Role, ownership, the IN_PROGRESS state and the file policy are enforced by the
 * backend on every request; nothing here is a security boundary.
 */
@Injectable({ providedIn: 'root' })
export class WorkEvidenceService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

  list(workOrderId: string): Observable<WorkEvidenceList> {
    return this.http.get<WorkEvidenceList>(`${this.baseUrl}/${workOrderId}/evidence`);
  }

  upload(workOrderId: string, file: File, evidenceType: EvidenceType): Observable<WorkEvidenceItem> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('evidenceType', evidenceType);
    return this.http.post<WorkEvidenceItem>(`${this.baseUrl}/${workOrderId}/evidence`, form);
  }
}
