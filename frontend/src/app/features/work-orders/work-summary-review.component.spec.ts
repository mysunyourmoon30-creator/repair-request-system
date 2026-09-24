import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { WorkOrder } from './work-order.models';
import { WorkSummaryReviewComponent } from './work-summary-review.component';

describe('WorkSummaryReviewComponent', () => {
  let httpMock: HttpTestingController;
  const workOrdersBaseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

  const item: WorkOrder = {
    workOrderId: 'wo-1',
    workOrderNo: 'WO-1',
    status: 'AWAITING_SUPERVISOR_REVIEW',
    repairRequestId: 'rr-1',
    repairRequestNo: 'RR-1',
    customerCode: 'CUST-1',
    siteCode: 'SITE-A',
    equipmentCode: null,
    rowVersion: 'v1',
    visits: [],
    acceptanceContactId: null,
  };

  const oneContact = [{ userId: 'req-1', displayName: 'req-1@example.test', email: 'req-1@example.test' }];
  const twoContacts = [
    { userId: 'req-1', displayName: 'req-1@example.test', email: 'req-1@example.test' },
    { userId: 'req-2', displayName: 'req-2@example.test', email: 'req-2@example.test' },
  ];

  function createComponent(): ComponentFixture<WorkSummaryReviewComponent> {
    TestBed.configureTestingModule({
      imports: [WorkSummaryReviewComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(WorkSummaryReviewComponent);
  }

  afterEach(() => httpMock.verify());

  function loadOneItem(): { fixture: ComponentFixture<WorkSummaryReviewComponent>; root: HTMLElement } {
    const fixture = createComponent();
    fixture.detectChanges();
    httpMock
      .expectOne((request) => request.url === workOrdersBaseUrl && request.params.get('status') === 'AWAITING_SUPERVISOR_REVIEW')
      .flush({ items: [item], page: 1, pageSize: 20, totalCount: 1 });
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  function buttonNamed(root: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  }

  /** Opens the contact dialog and flushes the eligible-contacts response. */
  function openDialogWithContacts(
    fixture: ComponentFixture<WorkSummaryReviewComponent>,
    root: HTMLElement,
    contacts: Array<{ userId: string; displayName: string; email: string }>,
  ): void {
    buttonNamed(root, 'Submit for Acceptance')!.click();
    fixture.detectChanges();
    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/eligible-acceptance-contacts`).flush(contacts);
    fixture.detectChanges();
  }

  it('requests the AWAITING_SUPERVISOR_REVIEW list on init and renders each item', () => {
    const { root } = loadOneItem();

    expect(root.textContent).toContain('WO-1');
    expect(root.textContent).toContain('SITE-A');
  });

  it('shows an empty message when there is nothing to review', () => {
    const fixture = createComponent();
    fixture.detectChanges();
    httpMock.expectOne((request) => request.url === workOrdersBaseUrl).flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No Work Orders are awaiting review');
  });

  it('fetches and shows the work summary when View Summary is clicked, then hides it on toggle', () => {
    const { fixture, root } = loadOneItem();

    buttonNamed(root, 'View Summary')!.click();
    fixture.detectChanges();

    httpMock
      .expectOne(`${workOrdersBaseUrl}/wo-1/work-summary`)
      .flush({ workSummaryId: 's1', workOrderId: 'wo-1', serviceVisitId: 'visit-1', revisionNo: 1, summaryText: 'Replaced the pump seal.', repairOutcomeCode: 'REPAIRED' });
    fixture.detectChanges();

    expect(root.textContent).toContain('Replaced the pump seal.');
    expect(root.textContent).toContain('REPAIRED');

    buttonNamed(root, 'Hide Summary')!.click();
    fixture.detectChanges();

    expect(root.textContent).not.toContain('Replaced the pump seal.');
  });

  // ---------------- Acceptance Contact dialog ----------------

  it('opens the dialog and fetches eligible contacts when Submit for Acceptance is clicked', () => {
    const { fixture, root } = loadOneItem();

    openDialogWithContacts(fixture, root, twoContacts);

    expect(root.querySelector('[role="dialog"]')).not.toBeNull();
    expect(root.textContent).toContain('req-1@example.test');
    expect(root.textContent).toContain('req-2@example.test');
  });

  it('pre-selects the single candidate when exactly one is eligible, but still requires explicit confirm', () => {
    const { fixture, root } = loadOneItem();

    openDialogWithContacts(fixture, root, oneContact);

    const select = root.querySelector('select') as HTMLSelectElement;
    expect(select.value).toBe('req-1');
    // Still requires the caller to submit the form — no auto-submit even with one candidate.
    expect(buttonNamed(root, 'Confirm Submit')).toBeDefined();
  });

  it('keeps Confirm disabled until a contact is selected when there is more than one candidate', () => {
    const { fixture, root } = loadOneItem();

    openDialogWithContacts(fixture, root, twoContacts);

    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(true);

    const select = root.querySelector('select') as HTMLSelectElement;
    select.value = 'req-2';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(false);
  });

  it('shows an alert and no form when no eligible contacts exist', () => {
    const { fixture, root } = loadOneItem();

    openDialogWithContacts(fixture, root, []);

    expect(root.textContent).toContain('No eligible Acceptance Contact was found');
    expect(root.querySelector('select')).toBeNull();
  });

  it('closes the dialog without a request when cancelled', () => {
    const { fixture, root } = loadOneItem();

    openDialogWithContacts(fixture, root, oneContact);
    buttonNamed(root, 'Cancel')!.click();
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).toBeNull();
  });

  it('submits for acceptance with the quoted rowVersion and the selected contact id, then removes the item from the list', () => {
    const { fixture, root } = loadOneItem();
    openDialogWithContacts(fixture, root, oneContact);

    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    const req = httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/submit-for-acceptance`);
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toEqual({ acceptanceContactId: 'req-1' });
    req.flush({});
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).toBeNull();
    expect(root.textContent).toContain('No Work Orders are awaiting review');
  });

  it('shows the server message and reloads when Submit for Acceptance returns a STATE_CONFLICT', () => {
    const { fixture, root } = loadOneItem();
    openDialogWithContacts(fixture, root, oneContact);

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock
      .expectOne(`${workOrdersBaseUrl}/wo-1/submit-for-acceptance`)
      .flush({ code: 'STATE_CONFLICT', detail: 'This Work Order was already submitted for acceptance.' }, { status: 409, statusText: 'Conflict' });
    httpMock.expectOne((request) => request.url === workOrdersBaseUrl).flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).toBeNull();
    expect(root.textContent).toContain('This Work Order was already submitted for acceptance.');
  });

  it('keeps the dialog open with the field error when the contact is no longer eligible (422)', () => {
    const { fixture, root } = loadOneItem();
    openDialogWithContacts(fixture, root, oneContact);

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock
      .expectOne(`${workOrdersBaseUrl}/wo-1/submit-for-acceptance`)
      .flush({ code: 'VALIDATION_FAILED', errors: { acceptanceContactId: ['no longer eligible'] } }, { status: 422, statusText: 'Unprocessable Entity' });
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).not.toBeNull();
    expect(root.textContent).toContain('no longer eligible');
  });

  it('shows a generic message, not the raw HTTP error, for a server error', () => {
    const { fixture, root } = loadOneItem();
    openDialogWithContacts(fixture, root, oneContact);

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/submit-for-acceptance`).flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root.textContent).toContain('Something went wrong');
    expect(root.textContent).not.toContain('Http failure');
  });
});
