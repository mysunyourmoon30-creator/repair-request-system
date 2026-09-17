import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { WorkOrderDetailComponent } from './work-order-detail.component';

describe('WorkOrderDetailComponent', () => {
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

  function createComponent(id: string | null): ComponentFixture<WorkOrderDetailComponent> {
    TestBed.configureTestingModule({
      imports: [WorkOrderDetailComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap(id ? { id } : {}) } },
        },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(WorkOrderDetailComponent);
  }

  afterEach(() => httpMock.verify());

  it('requests the Work Order by id and renders its fields', () => {
    const fixture = createComponent('abc-123');
    fixture.detectChanges();

    httpMock.expectOne(`${baseUrl}/abc-123`).flush({
      workOrderId: 'abc-123',
      workOrderNo: 'WO-1',
      status: 'OPEN',
      repairRequestId: 'r1',
      repairRequestNo: 'RR-1',
      customerCode: 'CUST-1',
      siteCode: 'SITE-A',
      equipmentCode: 'EQ-1',
      rowVersion: 'v1',
    });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('WO-1');
    expect(text).toContain('RR-1');
  });

  it('shows a not-found message on 404 (missing or out of scope)', () => {
    const fixture = createComponent('missing');
    fixture.detectChanges();

    httpMock.expectOne(`${baseUrl}/missing`).flush('Not Found', { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('does not exist');
  });

  it('shows an unauthorized message on 401/403', () => {
    const fixture = createComponent('abc-123');
    fixture.detectChanges();

    httpMock.expectOne(`${baseUrl}/abc-123`).flush('Forbidden', { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('not signed in');
  });
});
