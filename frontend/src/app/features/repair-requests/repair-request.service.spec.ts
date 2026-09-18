import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { RepairRequestService } from './repair-request.service';

describe('RepairRequestService', () => {
  let service: RepairRequestService;
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/repair-requests`;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(RepairRequestService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('requests the list with page, pageSize and status query params', () => {
    service.list(2, 20, 'APPROVED').subscribe();

    const req = httpMock.expectOne((request) => request.url === baseUrl);
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('20');
    expect(req.request.params.get('status')).toBe('APPROVED');
    req.flush({ items: [], page: 2, pageSize: 20, totalCount: 0 });
  });

  it('omits the status query param when none is selected', () => {
    service.list(1, 20, null).subscribe();

    const req = httpMock.expectOne((request) => request.url === baseUrl);
    expect(req.request.params.has('status')).toBe(false);
    req.flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  });

  it('requests a Repair Request by id and captures its ETag header', () => {
    let captured: { repairRequest: { status: string }; eTag: string | null } | undefined;
    service.get('rr-1').subscribe((result) => (captured = result));

    const req = httpMock.expectOne(`${baseUrl}/rr-1`);
    expect(req.request.method).toBe('GET');
    req.flush(
      { id: 'rr-1', status: 'APPROVED', requestNo: 'RR-2026-000001', rowVersion: 'AAAAAAAAB9E=' },
      { headers: { ETag: '"AAAAAAAAB9E="' } },
    );

    expect(captured?.repairRequest.status).toBe('APPROVED');
    expect(captured?.eTag).toBe('"AAAAAAAAB9E="');
  });

  it('converts with the given If-Match header and no body', () => {
    service.convert('rr-1', '"AAAAAAAAB9E="').subscribe();

    const req = httpMock.expectOne(`${baseUrl}/rr-1/convert-to-work-order`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"AAAAAAAAB9E="');
    expect(req.request.body).toBeNull();
    req.flush({ workOrderId: 'wo-1', workOrderNo: 'WO-2026-000001', status: 'OPEN' });
  });
});
