import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { WorkSummaryService } from './work-summary.service';

describe('WorkSummaryService', () => {
  let service: WorkSummaryService;
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(WorkSummaryService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('submits with the quoted If-Match header and both fields', () => {
    service.submit('wo-1', '"v1"', { summaryText: 'Replaced the pump seal.', repairOutcomeCode: 'REPAIRED' }).subscribe();

    const req = httpMock.expectOne(`${baseUrl}/wo-1/submit-work-summary`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toEqual({ summaryText: 'Replaced the pump seal.', repairOutcomeCode: 'REPAIRED' });
    req.flush({});
  });

  it('submits for acceptance with the quoted If-Match header and no body', () => {
    service.submitForAcceptance('wo-1', '"v2"').subscribe();

    const req = httpMock.expectOne(`${baseUrl}/wo-1/submit-for-acceptance`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"v2"');
    expect(req.request.body).toBeNull();
    req.flush({});
  });

  it('reads the work summary', () => {
    let result: unknown = 'unset';
    service.get('wo-1').subscribe((summary) => (result = summary));

    const req = httpMock.expectOne(`${baseUrl}/wo-1/work-summary`);
    expect(req.request.method).toBe('GET');
    req.flush({ workSummaryId: 's1', workOrderId: 'wo-1', serviceVisitId: 'v1', revisionNo: 1, summaryText: 'x', repairOutcomeCode: 'REPAIRED' });

    expect(result).toEqual({ workSummaryId: 's1', workOrderId: 'wo-1', serviceVisitId: 'v1', revisionNo: 1, summaryText: 'x', repairOutcomeCode: 'REPAIRED' });
  });

  it('maps a 404 to null instead of an error', () => {
    let result: unknown = 'unset';
    service.get('wo-1').subscribe((summary) => (result = summary));

    httpMock.expectOne(`${baseUrl}/wo-1/work-summary`).flush('Not Found', { status: 404, statusText: 'Not Found' });

    expect(result).toBeNull();
  });

  it('propagates a non-404 error', () => {
    let error: unknown = null;
    service.get('wo-1').subscribe({ error: (err) => (error = err) });

    httpMock.expectOne(`${baseUrl}/wo-1/work-summary`).flush('Forbidden', { status: 403, statusText: 'Forbidden' });

    expect(error).not.toBeNull();
  });
});
