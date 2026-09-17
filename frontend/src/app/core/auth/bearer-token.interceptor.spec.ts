import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { bearerTokenInterceptor } from './bearer-token.interceptor';

describe('bearerTokenInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.removeItem('accessToken');
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([bearerTokenInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.removeItem('accessToken');
  });

  it('attaches Authorization: Bearer <token> when a token is stored', () => {
    localStorage.setItem('accessToken', 'real-token-123');

    http.get('/api/v1/work-orders').subscribe();

    const req = httpMock.expectOne('/api/v1/work-orders');
    expect(req.request.headers.get('Authorization')).toBe('Bearer real-token-123');
    req.flush({});
  });

  it('sends no Authorization header when no token is stored', () => {
    http.get('/api/v1/work-orders').subscribe();

    const req = httpMock.expectOne('/api/v1/work-orders');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });
});
