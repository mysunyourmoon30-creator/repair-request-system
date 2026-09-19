import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { MyVisitsService } from './my-visits.service';

describe('MyVisitsService', () => {
  let service: MyVisitsService;
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/service-visits`;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(MyVisitsService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('requests the list with page and pageSize query params', () => {
    service.list(2, 20).subscribe();

    const req = httpMock.expectOne((request) => request.url === `${baseUrl}/mine`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('20');
    req.flush({ items: [], page: 2, pageSize: 20, totalCount: 0 });
  });

  it('checks in with the given If-Match header and an empty body', () => {
    service.checkIn('visit-1', '"v1"').subscribe();

    const req = httpMock.expectOne(`${baseUrl}/visit-1/check-in`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toBeNull();
    req.flush({});
  });
});
