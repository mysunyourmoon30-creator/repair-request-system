import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import {
  PagedTimeCorrections,
  RequestTimeCorrectionBody,
  TimeCorrection,
  TimeCorrectionTargets,
} from './time-correction.models';

/**
 * Time Correction endpoints (`docs/13` §4.29). Request (TIME-API-001) has no If-Match; Approve and Reject (TIME-API-002/003) send the
 * correction's own `rowVersion` as a quoted If-Match, exactly as the list item carried it. Nothing here retries: a failed or conflicting call
 * is reported to the screen, which asks for a new explicit decision.
 */
@Injectable({ providedIn: 'root' })
export class TimeCorrectionService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = `${environment.apiBaseUrl}/v1`;

  /** TIME-API-ADD-002: the caller's correctable sessions (with pause ids) of one Work Order. */
  requestTargets(workOrderId: string): Observable<TimeCorrectionTargets> {
    return this.http.get<TimeCorrectionTargets>(`${this.apiUrl}/work-orders/${workOrderId}/time-correction-targets`);
  }

  /** TIME-API-001. The body carries only the four client-supplied members; the old value, status and requester are server-derived. */
  request(workSessionId: string, body: RequestTimeCorrectionBody): Observable<TimeCorrection> {
    const payload: RequestTimeCorrectionBody =
      body.workSessionPauseId === undefined
        ? { timestampType: body.timestampType, newValue: body.newValue, reason: body.reason }
        : body;
    return this.http.post<TimeCorrection>(`${this.apiUrl}/work-sessions/${workSessionId}/time-corrections`, payload);
  }

  /** TIME-API-ADD-001: the Supervisor's Site-scoped PENDING requests, oldest first. */
  listPending(page: number, pageSize: number): Observable<PagedTimeCorrections> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedTimeCorrections>(`${this.apiUrl}/time-corrections`, { params });
  }

  /** TIME-API-002: empty body; If-Match = the correction's own row version. */
  approve(timeCorrectionId: string, rowVersion: string): Observable<TimeCorrection> {
    return this.http.post<TimeCorrection>(`${this.apiUrl}/time-corrections/${timeCorrectionId}/approve`, null, {
      headers: { 'If-Match': `"${rowVersion}"` },
    });
  }

  /** TIME-API-003: the decision reason is required (the backend answers 422 for a blank one). */
  reject(timeCorrectionId: string, rowVersion: string, decisionReason: string): Observable<TimeCorrection> {
    return this.http.post<TimeCorrection>(
      `${this.apiUrl}/time-corrections/${timeCorrectionId}/reject`,
      { decisionReason },
      { headers: { 'If-Match': `"${rowVersion}"` } },
    );
  }
}
