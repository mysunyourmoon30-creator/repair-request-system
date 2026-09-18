import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { RepairRequestListComponent } from './repair-request-list.component';

describe('RepairRequestListComponent', () => {
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/repair-requests`;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RepairRequestListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('renders rows from the first page on load', () => {
    const fixture = TestBed.createComponent(RepairRequestListComponent);
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === baseUrl).flush({
      items: [
        {
          id: '1',
          status: 'APPROVED',
          requestNo: 'RR-2026-000001',
          description: 'Pump leaking',
          rowVersion: 'v1',
        },
      ],
      page: 1,
      pageSize: 20,
      totalCount: 1,
    });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('RR-2026-000001');
    expect(text).toContain('Pump leaking');
  });

  it('re-requests with the selected status when the filter changes', () => {
    const fixture = TestBed.createComponent(RepairRequestListComponent);
    fixture.detectChanges();
    httpMock
      .expectOne((request) => request.url === baseUrl)
      .flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    fixture.detectChanges();

    const select: HTMLSelectElement = fixture.nativeElement.querySelector('select');
    select.value = 'APPROVED';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const req = httpMock.expectOne((request) => request.url === baseUrl);
    expect(req.request.params.get('status')).toBe('APPROVED');
    req.flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  });

  it('shows an unauthorized message on 401/403 without fabricating a login', () => {
    const fixture = TestBed.createComponent(RepairRequestListComponent);
    fixture.detectChanges();

    httpMock
      .expectOne((request) => request.url === baseUrl)
      .flush('Forbidden', { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('not signed in');
  });
});
