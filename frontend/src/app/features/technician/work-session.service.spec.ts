import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { WorkSessionService } from './work-session.service';

describe('WorkSessionService', () => {
  let service: WorkSessionService;
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/work-sessions`;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(WorkSessionService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('requests the current session, and maps a 204 to null', () => {
    let result: unknown = 'unset';
    service.current().subscribe((session) => (result = session));

    const req = httpMock.expectOne(`${baseUrl}/current`);
    expect(req.request.method).toBe('GET');
    req.flush(null, { status: 204, statusText: 'No Content' });

    expect(result).toBeNull();
  });

  it('pauses with the quoted If-Match header and a body that carries only the reason', () => {
    service.pause('session-1', '"v1"', 'Waiting for a part').subscribe();

    const req = httpMock.expectOne(`${baseUrl}/session-1/pause`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    // The client never sends a status or a time.
    expect(req.request.body).toEqual({ reason: 'Waiting for a part' });
    req.flush({});
  });
});
