import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { WorkOrderListComponent } from './work-order-list.component';

describe('WorkOrderListComponent', () => {
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WorkOrderListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('renders rows from the first page on load', () => {
    const fixture = TestBed.createComponent(WorkOrderListComponent);
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === baseUrl).flush({
      items: [
        {
          workOrderId: '1',
          workOrderNo: 'WO-1',
          status: 'OPEN',
          repairRequestId: 'r1',
          repairRequestNo: 'RR-1',
          customerCode: 'CUST-1',
          siteCode: 'SITE-A',
          equipmentCode: 'EQ-1',
          rowVersion: 'v1',
        },
      ],
      page: 1,
      pageSize: 20,
      totalCount: 1,
    });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('WO-1');
    expect(text).toContain('CUST-1');
  });

  it('re-requests with the selected status when the filter changes', () => {
    const fixture = TestBed.createComponent(WorkOrderListComponent);
    fixture.detectChanges();
    httpMock
      .expectOne((request) => request.url === baseUrl)
      .flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    fixture.detectChanges();

    const select: HTMLSelectElement = fixture.nativeElement.querySelector('select');
    select.value = 'OPEN';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const req = httpMock.expectOne((request) => request.url === baseUrl);
    expect(req.request.params.get('status')).toBe('OPEN');
    req.flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  });

  it('shows an unauthorized message on 401/403 without fabricating a login', () => {
    const fixture = TestBed.createComponent(WorkOrderListComponent);
    fixture.detectChanges();

    httpMock
      .expectOne((request) => request.url === baseUrl)
      .flush('Forbidden', { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('not signed in');
  });

  describe('Time Correction review entry (Supervisor only)', () => {
    function tokenWithPayload(payload: Record<string, unknown>): string {
      const base64url = (value: string) => btoa(value).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
      return `${base64url('{"alg":"HS256"}')}.${base64url(JSON.stringify(payload))}.signature`;
    }

    function entryFor(token: string | null): HTMLAnchorElement | null {
      if (token !== null) {
        localStorage.setItem('accessToken', token);
      }
      const fixture = TestBed.createComponent(WorkOrderListComponent);
      fixture.detectChanges();
      httpMock.expectOne((request) => request.url === baseUrl).flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
      fixture.detectChanges();
      return (fixture.nativeElement as HTMLElement).querySelector('a[href="/time-correction-review"]');
    }

    afterEach(() => localStorage.clear());

    it('shows one link to the review screen to a Supervisor', () => {
      const link = entryFor(tokenWithPayload({ sub: 'sup-1', role: 'SUPERVISOR' }));

      expect(link?.textContent).toContain('Time corrections awaiting review');
    });

    it.each(['TECHNICIAN', 'TEAM_LEAD', 'REQUESTER', 'COORDINATOR', 'APPROVER', 'ADMIN'])('shows no link to a %s', (role) => {
      expect(entryFor(tokenWithPayload({ sub: 'u-1', role }))).toBeNull();
    });

    it('shows no link when nobody is signed in', () => {
      expect(entryFor(null)).toBeNull();
    });
  });
});
