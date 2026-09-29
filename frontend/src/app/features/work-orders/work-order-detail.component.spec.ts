import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { WorkOrder } from './work-order.models';
import { WorkOrderDetailComponent } from './work-order-detail.component';

function tokenWithPayload(payload: Record<string, unknown>): string {
  const base64url = (value: string) => btoa(value).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${base64url('{"alg":"HS256"}')}.${base64url(JSON.stringify(payload))}.signature`;
}

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
    acceptanceContactId: null,
    costSummaryRowVersion: null,
    costSummaryReviewedAt: null,
    closedAt: null,
    correctiveActionId: null,
    correctiveActionStatus: null,
    correctiveServiceVisitId: null,
    correctiveActionRowVersion: null,
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

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

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

  // ---------------- Accept (`docs/13` §4.16) ----------------

  const awaitingAcceptance: WorkOrder = { ...baseWorkOrder, status: 'AWAITING_CUSTOMER_ACCEPTANCE', acceptanceContactId: 'req-1' };

  function buttonNamed(root: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  }

  it('shows Accept only when the signed-in Requester is the designated contact and the Work Order awaits acceptance', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'req-1', role: 'REQUESTER' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, awaitingAcceptance);

    expect(buttonNamed(fixture.nativeElement as HTMLElement, 'Accept')).toBeDefined();
  });

  it('hides Accept for a different signed-in user, even if AWAITING_CUSTOMER_ACCEPTANCE', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'someone-else', role: 'REQUESTER' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, awaitingAcceptance);

    expect(buttonNamed(fixture.nativeElement as HTMLElement, 'Accept')).toBeUndefined();
  });

  it('hides Accept for the designated contact\'s own id when signed in with a non-REQUESTER role', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'req-1', role: 'SUPERVISOR' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, awaitingAcceptance);

    expect(buttonNamed(fixture.nativeElement as HTMLElement, 'Accept')).toBeUndefined();
  });

  it('hides Accept when the Work Order is not AWAITING_CUSTOMER_ACCEPTANCE, even for the designated contact', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'req-1', role: 'REQUESTER' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, { ...awaitingAcceptance, status: 'COMPLETED' });

    expect(buttonNamed(fixture.nativeElement as HTMLElement, 'Accept')).toBeUndefined();
  });

  it('accepts with no body and the quoted rowVersion, and re-renders as COMPLETED', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'req-1', role: 'REQUESTER' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, awaitingAcceptance);
    const root = fixture.nativeElement as HTMLElement;

    buttonNamed(root, 'Accept')!.click();

    const req = httpMock.expectOne(`${baseUrl}/abc-123/accept`);
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toBeNull();
    req.flush({ ...awaitingAcceptance, status: 'COMPLETED', rowVersion: 'v2' });
    fixture.detectChanges();

    expect(root.textContent).toContain('COMPLETED');
    expect(buttonNamed(root, 'Accept')).toBeUndefined();
  });

  it('shows a distinct message when Accept returns 409, without hiding the surrounding page', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'req-1', role: 'REQUESTER' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, awaitingAcceptance);
    const root = fixture.nativeElement as HTMLElement;

    buttonNamed(root, 'Accept')!.click();
    httpMock.expectOne(`${baseUrl}/abc-123/accept`).flush('Conflict', { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root.textContent).toContain('changed or is no longer in a valid state');
  });

  // ---------------- Reject (`docs/13` §4.17) ----------------

  function rejectForm(root: HTMLElement): HTMLFormElement {
    return Array.from(root.querySelectorAll('form')).find((form) => form.textContent?.includes('Reject'))!;
  }

  it('shows Reject alongside Accept, under the same designated-contact gate', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'req-1', role: 'REQUESTER' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, awaitingAcceptance);
    const root = fixture.nativeElement as HTMLElement;

    expect(root.textContent).toContain('Reject');
    expect(rejectForm(root).querySelector('input[required]')).not.toBeNull();
  });

  it('hides Reject for a different signed-in user, even if AWAITING_CUSTOMER_ACCEPTANCE', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'someone-else', role: 'REQUESTER' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, awaitingAcceptance);

    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Reject');
  });

  it('rejects with the entered reason and the quoted rowVersion, and re-renders as CORRECTIVE_ACTION_REQUIRED', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'req-1', role: 'REQUESTER' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, awaitingAcceptance);
    const root = fixture.nativeElement as HTMLElement;

    const form = rejectForm(root);
    form.querySelector('input')!.value = 'Leak persists after the fix.';
    form.dispatchEvent(new Event('submit'));

    const req = httpMock.expectOne(`${baseUrl}/abc-123/reject`);
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toEqual({ decisionReason: 'Leak persists after the fix.' });
    req.flush({ ...awaitingAcceptance, status: 'CORRECTIVE_ACTION_REQUIRED', rowVersion: 'v2' });
    fixture.detectChanges();

    expect(root.textContent).toContain('CORRECTIVE_ACTION_REQUIRED');
    expect(buttonNamed(root, 'Accept')).toBeUndefined();
  });

  it('disables Accept and Reject while a Reject request is in flight, preventing a double submit', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'req-1', role: 'REQUESTER' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, awaitingAcceptance);
    const root = fixture.nativeElement as HTMLElement;

    const form = rejectForm(root);
    form.querySelector('input')!.value = 'Leak persists after the fix.';
    form.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    expect(buttonNamed(root, 'Accept')!.disabled).toBe(true);
    expect(form.querySelector('button')!.disabled).toBe(true);

    httpMock.expectOne(`${baseUrl}/abc-123/reject`).flush({ ...awaitingAcceptance, status: 'CORRECTIVE_ACTION_REQUIRED', rowVersion: 'v2' });
  });

  it('shows the 422 field error when the decision reason is rejected by the server', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'req-1', role: 'REQUESTER' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, awaitingAcceptance);
    const root = fixture.nativeElement as HTMLElement;

    const form = rejectForm(root);
    form.querySelector('input')!.value = 'x';
    form.dispatchEvent(new Event('submit'));

    httpMock
      .expectOne(`${baseUrl}/abc-123/reject`)
      .flush({ errors: { decisionReason: ['A reason of 1 to 1000 characters is required.'] } }, { status: 422, statusText: 'Unprocessable Entity' });
    fixture.detectChanges();

    expect(root.textContent).toContain('A reason of 1 to 1000 characters is required.');
  });

  it('shows a message and reloads the current Work Order when Reject returns 409 (stale/changed)', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'req-1', role: 'REQUESTER' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, awaitingAcceptance);
    const root = fixture.nativeElement as HTMLElement;

    const form = rejectForm(root);
    form.querySelector('input')!.value = 'Leak persists after the fix.';
    form.dispatchEvent(new Event('submit'));

    httpMock.expectOne(`${baseUrl}/abc-123/reject`).flush('Conflict', { status: 409, statusText: 'Conflict' });
    httpMock.expectOne(`${baseUrl}/abc-123`).flush({ ...awaitingAcceptance, status: 'COMPLETED', rowVersion: 'v2' });
    fixture.detectChanges();

    expect(root.textContent).toContain('changed or is no longer in a valid state');
    expect(root.textContent).toContain('COMPLETED');
  });

  // ---------------- Prepare Cost Summary (`docs/13` §4.18) ----------------

  const completedWorkOrder: WorkOrder = { ...baseWorkOrder, status: 'COMPLETED', costSummaryRowVersion: null, costSummaryReviewedAt: null };

  function costSummaryForm(root: HTMLElement): HTMLFormElement {
    return Array.from(root.querySelectorAll('form')).find((form) => form.textContent?.includes('Save Cost Summary'))!;
  }

  function fillCostSummaryForm(form: HTMLFormElement, totalAmount: string, currencyCode: string, note = ''): void {
    const inputs = form.querySelectorAll('input');
    (inputs[0] as HTMLInputElement).value = totalAmount;
    (inputs[1] as HTMLInputElement).value = currencyCode;
    (inputs[2] as HTMLInputElement).value = note;
    form.dispatchEvent(new Event('submit'));
  }

  it('shows Prepare Cost Summary only for a signed-in Team Lead on a COMPLETED, not-yet-reviewed Work Order', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, completedWorkOrder);

    expect(costSummaryForm(fixture.nativeElement as HTMLElement)).toBeDefined();
  });

  it('pre-fills the Prepare form with the existing Cost Summary when one already exists', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    fixture.detectChanges();
    httpMock
      .expectOne(`${baseUrl}/abc-123`)
      .flush({ ...completedWorkOrder, costSummaryRowVersion: 'cs-v1', costSummaryReviewedAt: null });
    fixture.detectChanges();

    httpMock.expectOne(`${baseUrl}/abc-123/cost-summary`).flush({
      costSummaryId: 'cs-1',
      workOrderId: 'abc-123',
      totalAmount: 1234.56,
      currencyCode: 'USD',
      note: 'Parts and labor.',
      preparedBy: 'tl-1',
      preparedAt: '2026-09-27T00:00:00Z',
      reviewedBy: null,
      reviewedAt: null,
      rowVersion: 'cs-v1',
    });
    fixture.detectChanges();

    const form = costSummaryForm(fixture.nativeElement as HTMLElement);
    const inputs = form.querySelectorAll('input');
    expect((inputs[0] as HTMLInputElement).value).toBe('1234.56');
    expect((inputs[1] as HTMLInputElement).value).toBe('USD');
    expect((inputs[2] as HTMLInputElement).value).toBe('Parts and labor.');
  });

  it('hides Prepare Cost Summary for a non-Team-Lead role', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'other-1', role: 'SUPERVISOR' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, completedWorkOrder);

    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Prepare Cost Summary');
  });

  it('hides Prepare Cost Summary when the Work Order is not COMPLETED', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, { ...completedWorkOrder, status: 'AWAITING_CUSTOMER_ACCEPTANCE' });

    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Prepare Cost Summary');
  });

  it('hides Prepare Cost Summary once the Cost Summary has been reviewed', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, { ...completedWorkOrder, costSummaryRowVersion: 'cs-v1', costSummaryReviewedAt: '2026-09-27T00:00:00Z' });

    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Prepare Cost Summary');
  });

  it('prepares for the first time using the Work Order\'s own quoted rowVersion, and stores the returned Cost Summary rowVersion', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, completedWorkOrder);
    const root = fixture.nativeElement as HTMLElement;

    fillCostSummaryForm(costSummaryForm(root), '1234.56', 'USD', 'Parts and labor.');

    const req = httpMock.expectOne(`${baseUrl}/abc-123/cost-summary`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toEqual({ totalAmount: 1234.56, currencyCode: 'USD', note: 'Parts and labor.' });

    req.flush({
      costSummaryId: 'cs-1',
      workOrderId: 'abc-123',
      totalAmount: 1234.56,
      currencyCode: 'USD',
      note: 'Parts and labor.',
      preparedBy: 'tl-1',
      preparedAt: '2026-09-27T00:00:00Z',
      reviewedBy: null,
      reviewedAt: null,
      rowVersion: 'cs-v1',
    });
    fixture.detectChanges();

    // Prepare Cost Summary stays visible (still not reviewed) — the next submit must use the Cost Summary's own token.
    fillCostSummaryForm(costSummaryForm(root), '2000', 'USD', '');
    expect(httpMock.expectOne(`${baseUrl}/abc-123/cost-summary`).request.headers.get('If-Match')).toBe('"cs-v1"');
  });

  it('disables Save while a Prepare request is in flight, preventing a double submit', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, completedWorkOrder);
    const root = fixture.nativeElement as HTMLElement;

    const form = costSummaryForm(root);
    fillCostSummaryForm(form, '1', 'USD');
    fixture.detectChanges();

    expect(form.querySelector('button')!.disabled).toBe(true);

    httpMock.expectOne(`${baseUrl}/abc-123/cost-summary`).flush({
      costSummaryId: 'cs-1',
      workOrderId: 'abc-123',
      totalAmount: 1,
      currencyCode: 'USD',
      note: null,
      preparedBy: 'tl-1',
      preparedAt: '2026-09-27T00:00:00Z',
      reviewedBy: null,
      reviewedAt: null,
      rowVersion: 'cs-v1',
    });
  });

  it('shows the 422 field error when totalAmount is rejected by the server', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, completedWorkOrder);
    const root = fixture.nativeElement as HTMLElement;

    fillCostSummaryForm(costSummaryForm(root), '-1', 'USD');

    httpMock
      .expectOne(`${baseUrl}/abc-123/cost-summary`)
      .flush({ errors: { totalAmount: ['totalAmount is required, must not be negative, and must have at most 2 decimal places.'] } }, { status: 422, statusText: 'Unprocessable Entity' });
    fixture.detectChanges();

    expect(root.textContent).toContain('must not be negative');
  });

  it('shows a message and reloads the current Work Order when Prepare returns 409 (stale/changed)', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, completedWorkOrder);
    const root = fixture.nativeElement as HTMLElement;

    fillCostSummaryForm(costSummaryForm(root), '1', 'USD');

    httpMock.expectOne((request) => request.method === 'PUT' && request.url === `${baseUrl}/abc-123/cost-summary`)
      .flush('Conflict', { status: 409, statusText: 'Conflict' });
    httpMock.expectOne(`${baseUrl}/abc-123`).flush({ ...completedWorkOrder, rowVersion: 'v2' });
    httpMock.expectOne((request) => request.method === 'GET' && request.url === `${baseUrl}/abc-123/cost-summary`)
      .flush({
        costSummaryId: 'cs-1',
        workOrderId: 'abc-123',
        totalAmount: 1,
        currencyCode: 'USD',
        note: null,
        preparedBy: 'tl-1',
        preparedAt: '2026-09-27T00:00:00Z',
        reviewedBy: null,
        reviewedAt: null,
        rowVersion: 'cs-v1',
      });
    fixture.detectChanges();

    expect(root.textContent).toContain('changed or is no longer in a valid state');
  });

  // ---------------- Close Work Order (`docs/13` §4.20) ----------------

  const closableWorkOrder: WorkOrder = {
    ...completedWorkOrder,
    costSummaryRowVersion: 'cs-v2',
    costSummaryReviewedAt: '2026-09-27T01:00:00Z',
  };

  function closeButton(root: HTMLElement): HTMLButtonElement | undefined {
    return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.trim() === 'Close Work Order');
  }

  function asSupervisor(): ComponentFixture<WorkOrderDetailComponent> {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'sup-1', role: 'SUPERVISOR' }));
    return createComponent('abc-123');
  }

  /** Renders the Work Order for the given role and returns whether the Close button is present (fresh TestBed each call). */
  function closeButtonShownFor(role: string, workOrder: WorkOrder): boolean {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'user-1', role }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, workOrder);
    const shown = closeButton(fixture.nativeElement as HTMLElement) !== undefined;

    httpMock.verify();
    TestBed.resetTestingModule();
    return shown;
  }

  it('shows Close Work Order to a Supervisor on a COMPLETED Work Order whose Cost Summary is reviewed', () => {
    const fixture = asSupervisor();
    loadWorkOrder(fixture, closableWorkOrder);

    expect(closeButton(fixture.nativeElement as HTMLElement)).toBeDefined();
  });

  it('hides Close Work Order for every role other than Supervisor', () => {
    const shownFor: string[] = [];
    for (const role of ['REQUESTER', 'APPROVER', 'COORDINATOR', 'TECHNICIAN', 'TEAM_LEAD', 'ADMINISTRATOR']) {
      if (closeButtonShownFor(role, closableWorkOrder)) {
        shownFor.push(role);
      }
    }

    expect(shownFor).toEqual([]);
  });

  it('hides Close Work Order while the Cost Summary is missing or not yet reviewed', () => {
    const noCostSummary = closeButtonShownFor('SUPERVISOR', { ...closableWorkOrder, costSummaryRowVersion: null, costSummaryReviewedAt: null });
    const unreviewedCostSummary = closeButtonShownFor('SUPERVISOR', { ...closableWorkOrder, costSummaryReviewedAt: null });

    expect({ noCostSummary, unreviewedCostSummary }).toEqual({ noCostSummary: false, unreviewedCostSummary: false });
  });

  it('hides Close Work Order unless the Work Order is COMPLETED (including once it is already CLOSED)', () => {
    const shownFor: string[] = [];
    for (const status of ['OPEN', 'IN_PROGRESS', 'AWAITING_CUSTOMER_ACCEPTANCE', 'CORRECTIVE_ACTION_REQUIRED', 'CLOSED', 'CANCELLED']) {
      if (closeButtonShownFor('SUPERVISOR', { ...closableWorkOrder, status })) {
        shownFor.push(status);
      }
    }

    expect(shownFor).toEqual([]);
  });

  it('closes using the Work Order\'s own quoted rowVersion with an empty body, then shows CLOSED and the closed time', () => {
    const fixture = asSupervisor();
    loadWorkOrder(fixture, closableWorkOrder);
    const root = fixture.nativeElement as HTMLElement;

    closeButton(root)!.click();

    const req = httpMock.expectOne(`${baseUrl}/abc-123/close`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toBeNull();

    req.flush({ ...closableWorkOrder, status: 'CLOSED', rowVersion: 'v2', closedAt: '2026-09-28T10:30:00Z' });
    fixture.detectChanges();

    expect(root.textContent).toContain('CLOSED');
    expect(root.textContent).toContain('Closed At');
    expect(root.textContent).toContain('2026-09-28T10:30:00Z');
    // Once CLOSED, the Close (and Prepare) controls are gone.
    expect(closeButton(root)).toBeUndefined();
  });

  it('never shows or requests closedBy', () => {
    const fixture = asSupervisor();
    loadWorkOrder(fixture, closableWorkOrder);
    const root = fixture.nativeElement as HTMLElement;

    closeButton(root)!.click();
    httpMock
      .expectOne(`${baseUrl}/abc-123/close`)
      .flush({ ...closableWorkOrder, status: 'CLOSED', rowVersion: 'v2', closedAt: '2026-09-28T10:30:00Z' });
    fixture.detectChanges();

    expect(root.textContent).not.toContain('closedBy');
    expect(root.textContent).not.toContain('Closed By');
  });

  it('disables Close Work Order while the request is in flight, and ignores a second click (double-submit)', () => {
    const fixture = asSupervisor();
    loadWorkOrder(fixture, closableWorkOrder);
    const root = fixture.nativeElement as HTMLElement;

    closeButton(root)!.click();
    fixture.detectChanges();
    expect(closeButton(root)!.disabled).toBe(true);

    closeButton(root)!.click();

    // Exactly one request despite two clicks (expectOne fails if there are two).
    httpMock
      .expectOne(`${baseUrl}/abc-123/close`)
      .flush({ ...closableWorkOrder, status: 'CLOSED', rowVersion: 'v2', closedAt: '2026-09-28T10:30:00Z' });
  });

  it('shows the server\'s guard-specific message and reloads the Work Order when Close returns 409 STATE_CONFLICT', () => {
    const fixture = asSupervisor();
    loadWorkOrder(fixture, closableWorkOrder);
    const root = fixture.nativeElement as HTMLElement;

    closeButton(root)!.click();
    httpMock
      .expectOne(`${baseUrl}/abc-123/close`)
      .flush(
        { code: 'STATE_CONFLICT', detail: 'The customer has not accepted this Work Order\'s current Work Summary, so it cannot be closed.' },
        { status: 409, statusText: 'Conflict' },
      );
    httpMock.expectOne((request) => request.method === 'GET' && request.url === `${baseUrl}/abc-123`).flush({ ...closableWorkOrder, rowVersion: 'v2' });
    fixture.detectChanges();

    expect(root.textContent).toContain('has not accepted this Work Order');
    // Re-enabled after the failure, so the Supervisor can retry once the prerequisite is fixed.
    expect(closeButton(root)!.disabled).toBe(false);
  });

  it('shows a concurrency message and reloads the Work Order when Close returns 409 CONCURRENCY_CONFLICT', () => {
    const fixture = asSupervisor();
    loadWorkOrder(fixture, closableWorkOrder);
    const root = fixture.nativeElement as HTMLElement;

    closeButton(root)!.click();
    httpMock.expectOne(`${baseUrl}/abc-123/close`).flush({ code: 'CONCURRENCY_CONFLICT' }, { status: 409, statusText: 'Conflict' });
    httpMock
      .expectOne((request) => request.method === 'GET' && request.url === `${baseUrl}/abc-123`)
      .flush({ ...closableWorkOrder, status: 'CLOSED', rowVersion: 'v3', closedAt: '2026-09-28T10:30:00Z' });
    fixture.detectChanges();

    expect(root.textContent).toContain('changed by another request');
    expect(root.textContent).toContain('CLOSED');
  });

  it('shows a permission message when Close returns 403', () => {
    const fixture = asSupervisor();
    loadWorkOrder(fixture, closableWorkOrder);
    const root = fixture.nativeElement as HTMLElement;

    closeButton(root)!.click();
    httpMock.expectOne(`${baseUrl}/abc-123/close`).flush({ code: 'ACCESS_DENIED' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(root.textContent).toContain('You do not have permission to close this Work Order.');
  });

  it('shows a not-found message when Close returns 404, without reloading', () => {
    const fixture = asSupervisor();
    loadWorkOrder(fixture, closableWorkOrder);
    const root = fixture.nativeElement as HTMLElement;

    closeButton(root)!.click();
    httpMock.expectOne(`${baseUrl}/abc-123/close`).flush({ code: 'NOT_FOUND' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(root.textContent).toContain('not found, or is outside your data scope');
  });

  // ---------------- Corrective Action: Submit Plan / Approve Plan (`docs/13` §4.21) ----------------

  const correctiveActionsBaseUrl = `${environment.apiBaseUrl}/v1/corrective-actions`;

  const draftCorrectiveAction: WorkOrder = {
    ...baseWorkOrder,
    status: 'CORRECTIVE_ACTION_REQUIRED',
    correctiveActionId: 'ca-1',
    correctiveActionStatus: 'DRAFT',
  };

  const pendingCorrectiveAction: WorkOrder = { ...draftCorrectiveAction, correctiveActionStatus: 'PENDING_PLAN_APPROVAL' };

  const correctiveActionResponse = {
    correctiveActionId: 'ca-1',
    workOrderId: 'abc-123',
    cycleNo: 1,
    status: 'PENDING_PLAN_APPROVAL',
    ownerTeamLeadId: 'tl-1',
    planText: 'Replace the seal.',
    planFileAssetId: 'file-1',
    approvedBy: null,
    approvedAt: null,
    workOrderRowVersion: 'v2',
    correctiveServiceVisitId: null,
  };

  function submitPlanForm(root: HTMLElement): HTMLFormElement {
    return Array.from(root.querySelectorAll('form')).find((form) => form.textContent?.includes('Submit Plan'))!;
  }

  function fillSubmitPlanForm(form: HTMLFormElement, planText: string, planFileAssetId: string): void {
    const textarea = form.querySelector('textarea') as HTMLTextAreaElement;
    const input = form.querySelector('input') as HTMLInputElement;
    textarea.value = planText;
    input.value = planFileAssetId;
    form.dispatchEvent(new Event('submit'));
  }

  function approvePlanButton(root: HTMLElement): HTMLButtonElement | undefined {
    return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.trim() === 'Approve Corrective Plan');
  }

  it('shows Submit Corrective Plan only for a signed-in Team Lead while the Corrective Action is DRAFT', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, draftCorrectiveAction);

    expect(submitPlanForm(fixture.nativeElement as HTMLElement)).toBeDefined();
  });

  it('hides Submit Corrective Plan for a non-Team-Lead role, and once the Corrective Action is no longer DRAFT', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'sup-1', role: 'SUPERVISOR' }));
    const asSupervisorFixture = createComponent('abc-123');
    loadWorkOrder(asSupervisorFixture, draftCorrectiveAction);
    expect((asSupervisorFixture.nativeElement as HTMLElement).textContent).not.toContain('Submit Corrective Plan');

    httpMock.verify();
    TestBed.resetTestingModule();
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const pendingFixture = createComponent('abc-123');
    loadWorkOrder(pendingFixture, pendingCorrectiveAction);
    expect((pendingFixture.nativeElement as HTMLElement).textContent).not.toContain('Submit Corrective Plan');
  });

  it("submits the plan using the Work Order's own quoted rowVersion, then reloads the Work Order", () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, draftCorrectiveAction);
    const root = fixture.nativeElement as HTMLElement;

    fillSubmitPlanForm(submitPlanForm(root), 'Replace the seal.', 'file-1');

    const req = httpMock.expectOne(`${correctiveActionsBaseUrl}/ca-1/submit-plan`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toEqual({ planText: 'Replace the seal.', planFileAssetId: 'file-1' });
    req.flush(correctiveActionResponse);

    httpMock.expectOne(`${baseUrl}/abc-123`).flush(pendingCorrectiveAction);
    fixture.detectChanges();

    expect(root.textContent).toContain('PENDING_PLAN_APPROVAL');
  });

  it('disables Submit Plan while the request is in flight, preventing a double submit', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, draftCorrectiveAction);
    const root = fixture.nativeElement as HTMLElement;

    const form = submitPlanForm(root);
    fillSubmitPlanForm(form, 'Plan.', 'file-1');
    fixture.detectChanges();

    expect(form.querySelector('button')!.disabled).toBe(true);

    httpMock.expectOne(`${correctiveActionsBaseUrl}/ca-1/submit-plan`).flush(correctiveActionResponse);
    httpMock.expectOne(`${baseUrl}/abc-123`).flush(pendingCorrectiveAction);
  });

  it('shows the server message and reloads the Work Order when Submit Plan returns 409', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, draftCorrectiveAction);
    const root = fixture.nativeElement as HTMLElement;

    fillSubmitPlanForm(submitPlanForm(root), 'Plan.', 'file-1');
    httpMock
      .expectOne(`${correctiveActionsBaseUrl}/ca-1/submit-plan`)
      .flush({ code: 'STATE_CONFLICT', detail: "This Corrective Action's plan has already been submitted." }, { status: 409, statusText: 'Conflict' });
    httpMock.expectOne(`${baseUrl}/abc-123`).flush(pendingCorrectiveAction);
    fixture.detectChanges();

    expect(root.textContent).toContain('has already been submitted');
  });

  it('shows a 422 field error when Submit Plan is rejected for a missing plan file', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, draftCorrectiveAction);
    const root = fixture.nativeElement as HTMLElement;

    fillSubmitPlanForm(submitPlanForm(root), 'Plan.', 'file-1');
    httpMock
      .expectOne(`${correctiveActionsBaseUrl}/ca-1/submit-plan`)
      .flush({ errors: { planFileAssetId: ['The plan file was not found.'] } }, { status: 422, statusText: 'Unprocessable Entity' });
    fixture.detectChanges();

    expect(root.textContent).toContain('The plan file was not found.');
  });

  it('shows Approve Corrective Plan only for a signed-in Supervisor while the Corrective Action is PENDING_PLAN_APPROVAL', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'sup-1', role: 'SUPERVISOR' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, pendingCorrectiveAction);

    expect(approvePlanButton(fixture.nativeElement as HTMLElement)).toBeDefined();
  });

  it('hides Approve Corrective Plan for a non-Supervisor role, and while the Corrective Action is still DRAFT', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'tl-1', role: 'TEAM_LEAD' }));
    const nonSupervisorFixture = createComponent('abc-123');
    loadWorkOrder(nonSupervisorFixture, pendingCorrectiveAction);
    expect(approvePlanButton(nonSupervisorFixture.nativeElement as HTMLElement)).toBeUndefined();

    httpMock.verify();
    TestBed.resetTestingModule();
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'sup-1', role: 'SUPERVISOR' }));
    const draftFixture = createComponent('abc-123');
    loadWorkOrder(draftFixture, draftCorrectiveAction);
    expect(approvePlanButton(draftFixture.nativeElement as HTMLElement)).toBeUndefined();
  });

  it("approves using the Work Order's own quoted rowVersion with an empty body, then reloads the Work Order", () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'sup-1', role: 'SUPERVISOR' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, pendingCorrectiveAction);
    const root = fixture.nativeElement as HTMLElement;

    approvePlanButton(root)!.click();

    const req = httpMock.expectOne(`${correctiveActionsBaseUrl}/ca-1/approve-plan`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toBeNull();
    req.flush({ ...correctiveActionResponse, status: 'APPROVED', approvedBy: 'sup-1', approvedAt: '2026-09-29T09:00:00Z' });

    httpMock.expectOne(`${baseUrl}/abc-123`).flush({ ...pendingCorrectiveAction, correctiveActionStatus: 'APPROVED' });
    fixture.detectChanges();

    expect(root.textContent).toContain('APPROVED');
    expect(approvePlanButton(root)).toBeUndefined();
  });

  it('disables Approve Corrective Plan while the request is in flight, ignoring a second click (double-submit)', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'sup-1', role: 'SUPERVISOR' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, pendingCorrectiveAction);
    const root = fixture.nativeElement as HTMLElement;

    approvePlanButton(root)!.click();
    fixture.detectChanges();
    expect(approvePlanButton(root)!.disabled).toBe(true);

    approvePlanButton(root)!.click();

    httpMock.expectOne(`${correctiveActionsBaseUrl}/ca-1/approve-plan`).flush({ ...correctiveActionResponse, status: 'APPROVED' });
    httpMock.expectOne(`${baseUrl}/abc-123`).flush({ ...pendingCorrectiveAction, correctiveActionStatus: 'APPROVED' });
  });

  it('shows a permission message when Approve Plan returns 403', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'sup-1', role: 'SUPERVISOR' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, pendingCorrectiveAction);
    const root = fixture.nativeElement as HTMLElement;

    approvePlanButton(root)!.click();
    httpMock.expectOne(`${correctiveActionsBaseUrl}/ca-1/approve-plan`).flush({ code: 'ACCESS_DENIED' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(root.textContent).toContain('You do not have permission to perform this action.');
  });

  // ---------------- Corrective Action: Schedule Rework (`docs/13` §4.22) ----------------

  const approvedCorrectiveAction: WorkOrder = {
    ...draftCorrectiveAction,
    correctiveActionStatus: 'APPROVED',
    correctiveActionRowVersion: 'ca-v1',
  };

  const scheduleReworkResponse = {
    ...correctiveActionResponse,
    status: 'APPROVED',
    approvedBy: 'sup-1',
    approvedAt: '2026-09-29T09:00:00Z',
    correctiveServiceVisitId: 'visit-2',
  };

  function scheduleReworkForm(root: HTMLElement): HTMLFormElement {
    return Array.from(root.querySelectorAll('form')).find((form) => form.textContent?.includes('Schedule Rework'))!;
  }

  function fillScheduleReworkForm(form: HTMLFormElement, teamId: string, technicianId: string, start: string, end: string): void {
    const inputs = form.querySelectorAll('input');
    (inputs[0] as HTMLInputElement).value = teamId;
    (inputs[1] as HTMLInputElement).value = technicianId;
    (inputs[2] as HTMLInputElement).value = start;
    (inputs[3] as HTMLInputElement).value = end;
    form.dispatchEvent(new Event('submit'));
  }

  it('shows Schedule Rework only for a signed-in Coordinator while APPROVED with no rework scheduled yet', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'coord-1', role: 'COORDINATOR' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, approvedCorrectiveAction);

    expect(scheduleReworkForm(fixture.nativeElement as HTMLElement)).toBeDefined();
  });

  it('hides Schedule Rework for a non-Coordinator role, and once a rework Visit is already linked', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'sup-1', role: 'SUPERVISOR' }));
    const nonCoordinatorFixture = createComponent('abc-123');
    loadWorkOrder(nonCoordinatorFixture, approvedCorrectiveAction);
    expect((nonCoordinatorFixture.nativeElement as HTMLElement).textContent).not.toContain('Schedule Rework');

    httpMock.verify();
    TestBed.resetTestingModule();
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'coord-1', role: 'COORDINATOR' }));
    const alreadyScheduledFixture = createComponent('abc-123');
    loadWorkOrder(alreadyScheduledFixture, { ...approvedCorrectiveAction, correctiveServiceVisitId: 'visit-2' });
    expect((alreadyScheduledFixture.nativeElement as HTMLElement).textContent).not.toContain('Schedule Rework');
  });

  it("schedules rework using the Corrective Action's own quoted correctiveActionRowVersion, then reloads the Work Order", () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'coord-1', role: 'COORDINATOR' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, approvedCorrectiveAction);
    const root = fixture.nativeElement as HTMLElement;

    fillScheduleReworkForm(scheduleReworkForm(root), 'team-2', 'tech-2', '2026-10-05T08:00', '2026-10-05T10:00');

    const req = httpMock.expectOne(`${correctiveActionsBaseUrl}/ca-1/schedule-rework`);
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"ca-v1"');
    expect(req.request.body).toEqual({
      assignedTeamId: 'team-2',
      assignedTechnicianId: 'tech-2',
      scheduledStartAt: '2026-10-05T08:00Z',
      scheduledEndAt: '2026-10-05T10:00Z',
    });
    req.flush(scheduleReworkResponse);

    httpMock.expectOne(`${baseUrl}/abc-123`).flush({ ...approvedCorrectiveAction, correctiveServiceVisitId: 'visit-2' });
    fixture.detectChanges();

    expect(root.textContent).toContain('visit-2');
    expect(scheduleReworkForm(root)).toBeUndefined();
  });

  it('disables Schedule Rework while the request is in flight, preventing a double submit', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'coord-1', role: 'COORDINATOR' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, approvedCorrectiveAction);
    const root = fixture.nativeElement as HTMLElement;

    const form = scheduleReworkForm(root);
    fillScheduleReworkForm(form, 'team-2', 'tech-2', '2026-10-05T08:00', '2026-10-05T10:00');
    fixture.detectChanges();

    expect(form.querySelector('button')!.disabled).toBe(true);

    httpMock.expectOne(`${correctiveActionsBaseUrl}/ca-1/schedule-rework`).flush(scheduleReworkResponse);
    httpMock.expectOne(`${baseUrl}/abc-123`).flush({ ...approvedCorrectiveAction, correctiveServiceVisitId: 'visit-2' });
  });

  it('shows the server message and reloads the Work Order when Schedule Rework returns 409', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'coord-1', role: 'COORDINATOR' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, approvedCorrectiveAction);
    const root = fixture.nativeElement as HTMLElement;

    fillScheduleReworkForm(scheduleReworkForm(root), 'team-2', 'tech-2', '2026-10-05T08:00', '2026-10-05T10:00');
    httpMock
      .expectOne(`${correctiveActionsBaseUrl}/ca-1/schedule-rework`)
      .flush({ code: 'STATE_CONFLICT', detail: "This Corrective Action's rework has already been scheduled." }, { status: 409, statusText: 'Conflict' });
    httpMock.expectOne(`${baseUrl}/abc-123`).flush(approvedCorrectiveAction);
    fixture.detectChanges();

    expect(root.textContent).toContain('has already been scheduled');
  });

  it('shows a 422 field error when Schedule Rework is rejected for an ineligible technician', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'coord-1', role: 'COORDINATOR' }));
    const fixture = createComponent('abc-123');
    loadWorkOrder(fixture, approvedCorrectiveAction);
    const root = fixture.nativeElement as HTMLElement;

    fillScheduleReworkForm(scheduleReworkForm(root), 'team-2', 'tech-2', '2026-10-05T08:00', '2026-10-05T10:00');
    httpMock
      .expectOne(`${correctiveActionsBaseUrl}/ca-1/schedule-rework`)
      .flush({ errors: { assignedTechnicianId: ['The technician must be an active Technician assigned to this Work Order’s Site.'] } }, { status: 422, statusText: 'Unprocessable Entity' });
    fixture.detectChanges();

    expect(root.textContent).toContain('The technician must be an active Technician');
  });
});
