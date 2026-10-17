import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { TimeCorrectionService } from './time-correction.service';

describe('TimeCorrectionService', () => {
  let service: TimeCorrectionService;
  let httpMock: HttpTestingController;
  const apiUrl = `${environment.apiBaseUrl}/v1`;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(TimeCorrectionService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('reads the request targets of one Work Order', () => {
    service.requestTargets('wo-1').subscribe();

    const req = httpMock.expectOne(`${apiUrl}/work-orders/wo-1/time-correction-targets`);
    expect(req.request.method).toBe('GET');
    req.flush({});
  });

  it('requests a CHECK_IN correction with exactly the client-supplied members and no pause id', () => {
    service
      .request('session-1', { timestampType: 'CHECK_IN', newValue: '2026-10-14T08:25:00.000Z', reason: 'Forgot to check in.' })
      .subscribe();

    const req = httpMock.expectOne(`${apiUrl}/work-sessions/session-1/time-corrections`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.has('If-Match')).toBe(false); // a Request has no If-Match
    expect(req.request.body).toEqual({
      timestampType: 'CHECK_IN',
      newValue: '2026-10-14T08:25:00.000Z',
      reason: 'Forgot to check in.',
    });
    expect(Object.keys(req.request.body as object).sort()).toEqual(['newValue', 'reason', 'timestampType']);
    req.flush({});
  });

  it('names the exact pause for a pause-type correction', () => {
    service
      .request('session-1', {
        timestampType: 'RESUME',
        workSessionPauseId: 'pause-2',
        newValue: '2026-10-14T08:40:00.000Z',
        reason: 'Resumed earlier.',
      })
      .subscribe();

    const req = httpMock.expectOne(`${apiUrl}/work-sessions/session-1/time-corrections`);
    expect(req.request.body.workSessionPauseId).toBe('pause-2');
    expect(req.request.body.timestampType).toBe('RESUME');
    req.flush({});
  });

  it('lists the pending requests with paging parameters', () => {
    service.listPending(2, 20).subscribe();

    const req = httpMock.expectOne((request) => request.url === `${apiUrl}/time-corrections`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('20');
    req.flush({ items: [], page: 2, pageSize: 20, totalCount: 0 });
  });

  it("approves with the correction's own row version as a quoted If-Match and no body", () => {
    service.approve('tc-1', 'AAAAAAAB').subscribe();

    const req = httpMock.expectOne(`${apiUrl}/time-corrections/tc-1/approve`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"AAAAAAAB"');
    expect(req.request.body).toBeNull();
    req.flush({});
  });

  it('rejects with the quoted If-Match and a body that carries only the decision reason', () => {
    service.reject('tc-1', 'AAAAAAAB', 'Not supported by the visit log.').subscribe();

    const req = httpMock.expectOne(`${apiUrl}/time-corrections/tc-1/reject`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"AAAAAAAB"');
    expect(req.request.body).toEqual({ decisionReason: 'Not supported by the visit log.' });
    req.flush({});
  });

  it('never retries a failed decision by itself', () => {
    let failed = false;
    service.approve('tc-1', 'v1').subscribe({ error: () => (failed = true) });

    httpMock.expectOne(`${apiUrl}/time-corrections/tc-1/approve`).flush({ code: 'CONCURRENCY_CONFLICT' }, { status: 409, statusText: 'Conflict' });

    expect(failed).toBe(true);
    httpMock.expectNone(`${apiUrl}/time-corrections/tc-1/approve`);
  });
});
