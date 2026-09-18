import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { WorkOrderService } from './work-order.service';

describe('WorkOrderService', () => {
  let service: WorkOrderService;
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(WorkOrderService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('requests the list with page, pageSize and status query params', () => {
    service.list(2, 20, 'OPEN').subscribe();

    const req = httpMock.expectOne((request) => request.url === baseUrl);
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('20');
    expect(req.request.params.get('status')).toBe('OPEN');
    req.flush({ items: [], page: 2, pageSize: 20, totalCount: 0 });
  });

  it('omits the status query param when none is selected', () => {
    service.list(1, 20, null).subscribe();

    const req = httpMock.expectOne((request) => request.url === baseUrl);
    expect(req.request.params.has('status')).toBe(false);
    req.flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  });

  it('requests a single Work Order by id', () => {
    service.get('abc-123').subscribe();

    const req = httpMock.expectOne(`${baseUrl}/abc-123`);
    expect(req.request.method).toBe('GET');
    req.flush({});
  });

  it('schedules with the given If-Match header and body', () => {
    service
      .schedule('wo-1', '"v1"', {
        ownerTeamId: 'team-1',
        assignedTechnicianId: 'tech-1',
        scheduledStartAt: '2026-10-01T08:00:00Z',
        scheduledEndAt: '2026-10-01T10:00:00Z',
      })
      .subscribe();

    const req = httpMock.expectOne(`${baseUrl}/wo-1/schedule`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body.ownerTeamId).toBe('team-1');
    req.flush({});
  });

  it('posts Service Visit actions to /api/v1/service-visits/{id}/{action}', () => {
    const visitsBaseUrl = `${environment.apiBaseUrl}/v1/service-visits`;

    service.cancelVisit('visit-1', '"vv1"', { reason: 'No longer needed' }).subscribe();
    const cancelReq = httpMock.expectOne(`${visitsBaseUrl}/visit-1/cancel`);
    expect(cancelReq.request.headers.get('If-Match')).toBe('"vv1"');
    expect(cancelReq.request.body).toEqual({ reason: 'No longer needed' });
    cancelReq.flush({});

    service.markMissed('visit-1', '"vv1"', { reason: 'No access' }).subscribe();
    httpMock.expectOne(`${visitsBaseUrl}/visit-1/mark-missed`).flush({});

    service.reassign('visit-1', '"vv1"', { reason: 'x', assignedTeamId: 't', assignedTechnicianId: 'u' }).subscribe();
    httpMock.expectOne(`${visitsBaseUrl}/visit-1/reassign`).flush({});

    service
      .reschedule('visit-1', '"vv1"', { reason: 'x', scheduledStartAt: '2026-10-02T08:00:00Z', scheduledEndAt: '2026-10-02T10:00:00Z' })
      .subscribe();
    httpMock.expectOne(`${visitsBaseUrl}/visit-1/reschedule`).flush({});

    service.decideMissed('visit-1', '"vv1"', { decision: 'NO_FOLLOW_UP', reason: 'done', newSchedule: null }).subscribe();
    httpMock.expectOne(`${visitsBaseUrl}/visit-1/missed-decision`).flush({});
  });
});
