import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { WorkOrder } from './work-order.models';
import { WorkOrderDetailComponent } from './work-order-detail.component';

describe('WorkOrderDetailComponent', () => {
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/work-orders`;
  const visitsBaseUrl = `${environment.apiBaseUrl}/v1/service-visits`;

  const baseWorkOrder: WorkOrder = {
    workOrderId: 'abc-123',
    workOrderNo: 'WO-1',
    status: 'OPEN',
    repairRequestId: 'r1',
    repairRequestNo: 'RR-1',
    customerCode: 'CUST-1',
    siteCode: 'SITE-A',
    equipmentCode: 'EQ-1',
    rowVersion: 'v1',
    visits: [],
  };

  const scheduledVisit = {
    serviceVisitId: 'visit-1',
    workOrderId: 'abc-123',
    visitType: 'INITIAL',
    status: 'SCHEDULED',
    assignedTeamId: 'team-1',
    assignedTechnicianId: 'tech-1',
    scheduledStartAt: '2026-10-01T08:00:00Z',
    scheduledEndAt: '2026-10-01T10:00:00Z',
    rescheduleReason: null,
    reassignReason: null,
    cancelReason: null,
    missedReason: null,
    completedAt: null,
    sourceMissedVisitId: null,
    missedDecisionCode: null,
    missedDecidedAt: null,
    rowVersion: 'visit-v1',
  };

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

  function loadWorkOrder(fixture: ComponentFixture<WorkOrderDetailComponent>, workOrder: WorkOrder): void {
    fixture.detectChanges();
    httpMock.expectOne(`${baseUrl}/${workOrder.workOrderId}`).flush(workOrder);
    fixture.detectChanges();
  }

  afterEach(() => httpMock.verify());

  it('requests the Work Order by id and renders its fields', () => {
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, baseWorkOrder);

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

  it('shows the Schedule form only while OPEN, and submits it with a quoted If-Match', () => {
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, baseWorkOrder);

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Schedule');
    const inputs = root.querySelectorAll('form')[0].querySelectorAll('input');
    inputs[0].value = '11111111-1111-1111-1111-111111111111';
    inputs[1].value = '22222222-2222-2222-2222-222222222222';
    inputs[2].value = '2026-10-01T08:00';
    inputs[3].value = '2026-10-01T10:00';

    root.querySelectorAll('form')[0].dispatchEvent(new Event('submit'));

    const req = httpMock.expectOne(`${baseUrl}/abc-123/schedule`);
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toEqual({
      ownerTeamId: '11111111-1111-1111-1111-111111111111',
      assignedTechnicianId: '22222222-2222-2222-2222-222222222222',
      scheduledStartAt: '2026-10-01T08:00Z',
      scheduledEndAt: '2026-10-01T10:00Z',
    });

    req.flush({ ...baseWorkOrder, status: 'SCHEDULED', visits: [scheduledVisit] });
    fixture.detectChanges();

    expect(root.textContent).toContain('SCHEDULED');
    expect(root.querySelectorAll('form').length).toBeGreaterThan(0); // visit action forms now render
  });

  it('does not show the Schedule form once SCHEDULED, and shows visit action controls instead', () => {
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, { ...baseWorkOrder, status: 'SCHEDULED', visits: [scheduledVisit] });

    const root = fixture.nativeElement as HTMLElement;
    const text = root.textContent ?? '';
    expect(text).not.toContain('Scheduled Start');
    expect(text).toContain('Reschedule');
    expect(text).toContain('Reassign');
    expect(text).toContain('Cancel Visit');
    expect(text).toContain('Mark Missed');
    expect(text).not.toContain('Decide Missed');
  });

  it('submits Cancel Visit with the visit\'s own quoted rowVersion, and re-renders the response', () => {
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, { ...baseWorkOrder, status: 'SCHEDULED', visits: [scheduledVisit] });

    const root = fixture.nativeElement as HTMLElement;
    const cancelForm = Array.from(root.querySelectorAll('form')).find((form) => form.textContent?.includes('Cancel Visit'))!;
    cancelForm.querySelector('input')!.value = 'No longer needed';
    cancelForm.dispatchEvent(new Event('submit'));

    const req = httpMock.expectOne(`${visitsBaseUrl}/visit-1/cancel`);
    expect(req.request.headers.get('If-Match')).toBe('"visit-v1"');
    expect(req.request.body).toEqual({ reason: 'No longer needed' });

    req.flush({ ...baseWorkOrder, status: 'SCHEDULED', visits: [{ ...scheduledVisit, status: 'CANCELLED', rowVersion: 'visit-v2' }] });
    fixture.detectChanges();

    expect(root.textContent).toContain('CANCELLED');
  });

  it('shows the Decide Missed form only for a MISSED visit with no decision yet', () => {
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, {
      ...baseWorkOrder,
      status: 'SCHEDULED',
      visits: [{ ...scheduledVisit, status: 'MISSED', missedReason: 'No access' }],
    });

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Decide Missed');
    expect(root.textContent).not.toContain('Reschedule');
  });

  it('shows a distinct message when an action returns 409', () => {
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, { ...baseWorkOrder, status: 'SCHEDULED', visits: [scheduledVisit] });

    const root = fixture.nativeElement as HTMLElement;
    const missedForm = Array.from(root.querySelectorAll('form')).find((form) => form.textContent?.includes('Mark Missed'))!;
    missedForm.querySelector('input')!.value = 'No access';
    missedForm.dispatchEvent(new Event('submit'));

    httpMock.expectOne(`${visitsBaseUrl}/visit-1/mark-missed`).flush('Conflict', { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root.textContent).toContain('changed or is no longer in a valid state');
  });
});
