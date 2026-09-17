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
});
