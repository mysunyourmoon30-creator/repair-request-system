import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { WorkSession } from './work-session.models';

/**
 * Calls S3-002's Technician-only endpoints: `GET /api/v1/work-sessions/current` (the caller's own non-checked-out
 * session, or `204` → `null` when there is none) and `POST /api/v1/work-sessions/{id}/pause` (WS-API-002). Pause
 * sends only the reason — never a status or a time; both are decided by the backend.
 */
@Injectable({ providedIn: 'root' })
export class WorkSessionService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/v1/work-sessions`;

  current(): Observable<WorkSession | null> {
    return this.http.get<WorkSession | null>(`${this.baseUrl}/current`);
  }

  pause(workSessionId: string, ifMatch: string, reason: string): Observable<WorkSession> {
    return this.http.post<WorkSession>(`${this.baseUrl}/${workSessionId}/pause`, { reason }, { headers: { 'If-Match': ifMatch } });
  }
}
