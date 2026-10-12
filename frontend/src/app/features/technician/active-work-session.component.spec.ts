import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { ActiveWorkSessionComponent } from './active-work-session.component';
import { WorkEvidenceItem } from '../work-orders/work-evidence.models';
import { WorkSession } from './work-session.models';

describe('ActiveWorkSessionComponent', () => {
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/work-sessions`;
  const workOrdersBaseUrl = `${environment.apiBaseUrl}/v1/work-orders`;

  const checkedIn: WorkSession = {
    workSessionId: 'session-1',
    serviceVisitId: 'visit-1',
    workOrderId: 'wo-1',
    workOrderNo: 'WO-1',
    siteCode: 'SITE-A',
    equipmentCode: null,
    status: 'CHECKED_IN',
    checkInAt: '2026-10-01T08:00:00Z',
    pauseStartAt: null,
    resumeAt: null,
    checkOutAt: null,
    pauses: [],
    rowVersion: 'v1',
    workOrderRowVersion: 'wov1',
  };

  const paused: WorkSession = {
    ...checkedIn,
    status: 'PAUSED',
    pauseStartAt: '2026-10-01T09:00:00Z',
    pauses: [{ workSessionPauseId: 'p1', pausedAt: '2026-10-01T09:00:00Z', pauseReason: 'Waiting for a part', resumedAt: null }],
    rowVersion: 'v2',
  };

  const checkedOut: WorkSession = {
    ...checkedIn,
    status: 'CHECKED_OUT',
    checkOutAt: '2026-10-01T17:00:00Z',
    rowVersion: 'v3',
  };

  function load(session: WorkSession | null): { fixture: ComponentFixture<ActiveWorkSessionComponent>; root: HTMLElement } {
    TestBed.configureTestingModule({
      imports: [ActiveWorkSessionComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(ActiveWorkSessionComponent);
    fixture.detectChanges();

    httpMock.expectOne(`${baseUrl}/current`).flush(session, session ? { status: 200, statusText: 'OK' } : { status: 204, statusText: 'No Content' });
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  function buttonNamed(root: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  }

  function typeInOpenDialog(fixture: ComponentFixture<ActiveWorkSessionComponent>, root: HTMLElement, reason: string): void {
    const textarea = root.querySelector('textarea')!;
    textarea.value = reason;
    textarea.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function openDialogAndType(fixture: ComponentFixture<ActiveWorkSessionComponent>, root: HTMLElement, reason: string): void {
    buttonNamed(root, 'Pause')!.click();
    fixture.detectChanges();
    typeInOpenDialog(fixture, root, reason);
  }

  afterEach(() => httpMock.verify());

  it('renders nothing when there is no current session', () => {
    const { root } = load(null);

    expect(root.textContent?.trim()).toBe('');
    expect(root.querySelector('button')).toBeNull();
  });

  it('shows the Pause and Check-out buttons for a CHECKED_IN session', () => {
    const { root } = load(checkedIn);

    expect(root.textContent).toContain('WO-1');
    expect(buttonNamed(root, 'Pause')).toBeDefined();
    expect(buttonNamed(root, 'Check-out')).toBeDefined();
    expect(root.querySelector('[role="dialog"]')).toBeNull();
  });

  it('shows the recorded pause, no Pause button, and a Resume button for a PAUSED session', () => {
    const { root } = load(paused);

    expect(root.textContent).toContain('Waiting for a part');
    expect(root.textContent).toContain('PAUSED');
    expect(buttonNamed(root, 'Pause')).toBeUndefined();
    expect(buttonNamed(root, 'Resume')).toBeDefined();
  });

  it('shows no Resume button for a CHECKED_IN session', () => {
    const { root } = load(checkedIn);

    expect(buttonNamed(root, 'Resume')).toBeUndefined();
  });

  it('shows no Pause or Check-out button for a PAUSED session', () => {
    const { root } = load(paused);

    expect(buttonNamed(root, 'Pause')).toBeUndefined();
    expect(buttonNamed(root, 'Check-out')).toBeUndefined();
  });

  it('shows only the Submit Work Summary button for a CHECKED_OUT session', () => {
    const { root } = load(checkedOut);

    expect(root.textContent).toContain('CHECKED_OUT');
    expect(buttonNamed(root, 'Pause')).toBeUndefined();
    expect(buttonNamed(root, 'Resume')).toBeUndefined();
    expect(buttonNamed(root, 'Check-out')).toBeUndefined();
    expect(buttonNamed(root, 'Submit Work Summary')).toBeDefined();
  });

  it('opens a dialog that keeps Confirm disabled for an empty or whitespace-only reason', () => {
    const { fixture, root } = load(checkedIn);

    openDialogAndType(fixture, root, '');
    expect(root.querySelector('[role="dialog"]')).not.toBeNull();
    expect(buttonNamed(root, 'Confirm Pause')!.disabled).toBe(true);

    typeInOpenDialog(fixture, root, '   \t ');
    expect(buttonNamed(root, 'Confirm Pause')!.disabled).toBe(true);

    typeInOpenDialog(fixture, root, 'Waiting for a part');
    expect(buttonNamed(root, 'Confirm Pause')!.disabled).toBe(false);
  });

  it('does not send a request for a whitespace-only reason, even if the form is submitted', () => {
    const { fixture, root } = load(checkedIn);
    openDialogAndType(fixture, root, '   ');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    httpMock.expectNone(`${baseUrl}/session-1/pause`);
  });

  it('pauses with the trimmed reason and the quoted rowVersion, sends no status or time, and re-renders as PAUSED', () => {
    const { fixture, root } = load(checkedIn);
    openDialogAndType(fixture, root, '  Waiting for a part  ');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    const req = httpMock.expectOne(`${baseUrl}/session-1/pause`);
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toEqual({ reason: 'Waiting for a part' });
    req.flush(paused);
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).toBeNull();
    expect(root.textContent).toContain('PAUSED');
    expect(buttonNamed(root, 'Pause')).toBeUndefined();
  });

  it('keeps the dialog open with a plain message when the backend rejects a blank reason (422)', () => {
    const { fixture, root } = load(checkedIn);
    openDialogAndType(fixture, root, 'x');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock.expectOne(`${baseUrl}/session-1/pause`).flush({ code: 'VALIDATION_FAILED', errors: { reason: ['required'] } }, { status: 422, statusText: 'Unprocessable Entity' });
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).not.toBeNull();
    expect(root.textContent).toContain('Please enter a reason for the pause.');
  });

  it('shows the server message and reloads the real state when the session is already paused (409 STATE_CONFLICT)', () => {
    const { fixture, root } = load(checkedIn);
    openDialogAndType(fixture, root, 'x');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock
      .expectOne(`${baseUrl}/session-1/pause`)
      .flush({ code: 'STATE_CONFLICT', detail: 'This Work Session is already paused.' }, { status: 409, statusText: 'Conflict' });
    httpMock.expectOne(`${baseUrl}/current`).flush(paused);
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).toBeNull();
    expect(root.textContent).toContain('This Work Session is already paused.');
    expect(buttonNamed(root, 'Pause')).toBeUndefined();
  });

  it('shows a plain message for a stale token (409 CONCURRENCY_CONFLICT) and reloads', () => {
    const { fixture, root } = load(checkedIn);
    openDialogAndType(fixture, root, 'x');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock.expectOne(`${baseUrl}/session-1/pause`).flush({ code: 'CONCURRENCY_CONFLICT' }, { status: 409, statusText: 'Conflict' });
    httpMock.expectOne(`${baseUrl}/current`).flush(checkedIn);
    fixture.detectChanges();

    expect(root.textContent).toContain('changed by another request');
    expect(root.textContent).not.toContain('Http failure');
  });

  it('shows a plain not-found message for 404 and reloads', () => {
    const { fixture, root } = load(checkedIn);
    openDialogAndType(fixture, root, 'x');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock.expectOne(`${baseUrl}/session-1/pause`).flush('Not Found', { status: 404, statusText: 'Not Found' });
    httpMock.expectOne(`${baseUrl}/current`).flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(root.textContent).toContain('no longer belongs to you');
    expect(root.textContent).not.toContain('Http failure');
  });

  it('shows a generic message, not the raw HTTP error, for a server error and keeps the dialog open', () => {
    const { fixture, root } = load(checkedIn);
    openDialogAndType(fixture, root, 'x');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock.expectOne(`${baseUrl}/session-1/pause`).flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).not.toBeNull();
    expect(root.textContent).toContain('Something went wrong');
    expect(root.textContent).not.toContain('Http failure');
  });

  it('closes the dialog without a request when cancelled', () => {
    const { fixture, root } = load(checkedIn);
    openDialogAndType(fixture, root, 'x');

    buttonNamed(root, 'Cancel')!.click();
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).toBeNull();
    expect(buttonNamed(root, 'Pause')).toBeDefined();
  });

  // ---------------- Resume ----------------

  it('resumes with the quoted rowVersion and no body, and re-renders as CHECKED_IN', () => {
    const { fixture, root } = load(paused);

    buttonNamed(root, 'Resume')!.click();

    const req = httpMock.expectOne(`${baseUrl}/session-1/resume`);
    expect(req.request.headers.get('If-Match')).toBe('"v2"');
    expect(req.request.body).toBeNull();
    req.flush(checkedIn);
    fixture.detectChanges();

    expect(root.textContent).toContain('CHECKED_IN');
    expect(buttonNamed(root, 'Resume')).toBeUndefined();
    expect(buttonNamed(root, 'Pause')).toBeDefined();
  });

  it('shows the server message and reloads the real state when the session is already resumed (409 STATE_CONFLICT)', () => {
    const { fixture, root } = load(paused);

    buttonNamed(root, 'Resume')!.click();
    httpMock
      .expectOne(`${baseUrl}/session-1/resume`)
      .flush({ code: 'STATE_CONFLICT', detail: 'This Work Session is not paused.' }, { status: 409, statusText: 'Conflict' });
    httpMock.expectOne(`${baseUrl}/current`).flush(checkedIn);
    fixture.detectChanges();

    expect(root.textContent).toContain('This Work Session is not paused.');
    expect(buttonNamed(root, 'Resume')).toBeUndefined();
  });

  it('shows a plain not-found message for Resume 404 and reloads', () => {
    const { fixture, root } = load(paused);

    buttonNamed(root, 'Resume')!.click();
    httpMock.expectOne(`${baseUrl}/session-1/resume`).flush('Not Found', { status: 404, statusText: 'Not Found' });
    httpMock.expectOne(`${baseUrl}/current`).flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(root.textContent).toContain('no longer belongs to you');
    expect(root.textContent).not.toContain('Http failure');
  });

  it('shows a generic message, not the raw HTTP error, for a Resume server error', () => {
    const { fixture, root } = load(paused);

    buttonNamed(root, 'Resume')!.click();
    httpMock.expectOne(`${baseUrl}/session-1/resume`).flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root.textContent).toContain('Something went wrong');
    expect(root.textContent).not.toContain('Http failure');
    // No reload on a generic failure — the session is still shown as PAUSED with its Resume button.
    expect(buttonNamed(root, 'Resume')).toBeDefined();
  });

  // ---------------- Check-out ----------------

  it('checks out with the quoted rowVersion and no body, and re-renders as CHECKED_OUT with no buttons', () => {
    const { fixture, root } = load(checkedIn);

    buttonNamed(root, 'Check-out')!.click();

    const req = httpMock.expectOne(`${baseUrl}/session-1/check-out`);
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toBeNull();
    req.flush(checkedOut);
    fixture.detectChanges();

    expect(root.textContent).toContain('CHECKED_OUT');
    expect(buttonNamed(root, 'Check-out')).toBeUndefined();
    expect(buttonNamed(root, 'Pause')).toBeUndefined();
    expect(buttonNamed(root, 'Resume')).toBeUndefined();
  });

  it('shows the server message and reloads the real state when the session is already checked out (409 STATE_CONFLICT)', () => {
    const { fixture, root } = load(checkedIn);

    buttonNamed(root, 'Check-out')!.click();
    httpMock
      .expectOne(`${baseUrl}/session-1/check-out`)
      .flush({ code: 'STATE_CONFLICT', detail: 'This Work Session is already checked out.' }, { status: 409, statusText: 'Conflict' });
    httpMock.expectOne(`${baseUrl}/current`).flush(checkedOut);
    fixture.detectChanges();

    expect(root.textContent).toContain('This Work Session is already checked out.');
    expect(buttonNamed(root, 'Check-out')).toBeUndefined();
  });

  it('shows a plain not-found message for Check-out 404 and reloads', () => {
    const { fixture, root } = load(checkedIn);

    buttonNamed(root, 'Check-out')!.click();
    httpMock.expectOne(`${baseUrl}/session-1/check-out`).flush('Not Found', { status: 404, statusText: 'Not Found' });
    httpMock.expectOne(`${baseUrl}/current`).flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(root.textContent).toContain('no longer belongs to you');
    expect(root.textContent).not.toContain('Http failure');
  });

  it('shows a generic message, not the raw HTTP error, for a Check-out server error', () => {
    const { fixture, root } = load(checkedIn);

    buttonNamed(root, 'Check-out')!.click();
    httpMock.expectOne(`${baseUrl}/session-1/check-out`).flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root.textContent).toContain('Something went wrong');
    expect(root.textContent).not.toContain('Http failure');
    // No reload on a generic failure — the session is still shown as CHECKED_IN with its Check-out button.
    expect(buttonNamed(root, 'Check-out')).toBeDefined();
  });

  // ---------------- Submit Work Summary ----------------

  const evidenceItem = (id: string, overrides: Partial<WorkEvidenceItem> = {}): WorkEvidenceItem => ({
    evidenceId: id,
    serviceVisitId: 'visit-1',
    evidenceType: 'AFTER_WORK',
    fileName: `${id}.png`,
    mimeType: 'image/png',
    sizeBytes: 2048,
    scanStatus: 'CLEAN',
    bound: false,
    workSummaryId: null,
    slotNo: null,
    uploadedAt: '2026-10-01T08:00:00Z',
    ...overrides,
  });

  function checkboxes(root: HTMLElement): HTMLInputElement[] {
    return Array.from(root.querySelectorAll<HTMLInputElement>('app-work-evidence-selection input[type="checkbox"]'));
  }

  function toggleEvidence(fixture: ComponentFixture<ActiveWorkSessionComponent>, root: HTMLElement, index: number): void {
    checkboxes(root)[index].click();
    fixture.detectChanges();
  }

  /**
   * Opens the Submit Work Summary dialog (answering the evidence list request the selection list makes), then fills the summary and the
   * outcome. With the default list the single CLEAN item is selected, so the dialog is ready to submit.
   */
  function openSummaryDialogAndType(
    fixture: ComponentFixture<ActiveWorkSessionComponent>,
    root: HTMLElement,
    summary: string,
    outcome: string,
    listed: WorkEvidenceItem[] = [evidenceItem('e1')],
    selectFirst = true,
  ): void {
    const wasOpen = root.querySelector('[role="dialog"]') !== null;
    buttonNamed(root, 'Submit Work Summary')!.click();
    fixture.detectChanges();
    if (!wasOpen) {
      httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/evidence`).flush({ items: listed });
      fixture.detectChanges();
      if (selectFirst && checkboxes(root).some((box) => !box.disabled)) {
        toggleEvidence(fixture, root, checkboxes(root).findIndex((box) => !box.disabled));
      }
    }

    const textarea = root.querySelector('textarea')!;
    textarea.value = summary;
    textarea.dispatchEvent(new Event('input'));
    const select = root.querySelector('select')!;
    select.value = outcome;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  it('offers the repair outcome as a closed dropdown of exactly the six allowed codes, never free text', () => {
    const { fixture, root } = load(checkedOut);

    openSummaryDialogAndType(fixture, root, '', '', [], false);

    const select = root.querySelector('select')!;
    const optionValues = Array.from(select.querySelectorAll('option'))
      .map((option) => option.value)
      .filter((value) => value !== '');
    expect(optionValues).toEqual(['REPAIRED', 'TEMPORARY_FIX', 'PARTS_REQUIRED', 'NO_FAULT_FOUND', 'NOT_REPAIRABLE', 'FOLLOW_UP_REQUIRED']);

    // No free-text input exists anywhere in the dialog for the outcome — only the summary textarea; the only inputs are evidence checkboxes.
    expect(Array.from(root.querySelectorAll('input')).every((input) => input.type === 'checkbox')).toBe(true);
  });

  it('opens the Submit Work Summary dialog and keeps Confirm disabled until both fields are filled', () => {
    const { fixture, root } = load(checkedOut);

    openSummaryDialogAndType(fixture, root, '', '');
    expect(root.querySelector('[role="dialog"]')).not.toBeNull();
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(true);

    openSummaryDialogAndType(fixture, root, 'Replaced the pump seal.', '');
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(true);

    openSummaryDialogAndType(fixture, root, 'Replaced the pump seal.', 'REPAIRED');
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(false);
  });

  // ---------------- evidence selection in the dialog ----------------

  it('lists the current Visit\'s evidence with every status: only CLEAN is selectable, PENDING and FAILED stay visible but disabled', () => {
    const { fixture, root } = load(checkedOut);

    openSummaryDialogAndType(
      fixture,
      root,
      'Replaced the pump seal.',
      'REPAIRED',
      [
        evidenceItem('clean', { fileName: 'clean.png' }),
        evidenceItem('pending', { fileName: 'pending.png', scanStatus: 'PENDING' }),
        evidenceItem('failed', { fileName: 'failed.png', scanStatus: 'FAILED' }),
        evidenceItem('frozen', { fileName: 'frozen.png', bound: true, slotNo: 1 }),
        evidenceItem('other-visit', { fileName: 'elsewhere.png', serviceVisitId: 'visit-0' }),
      ],
      false,
    );

    const text = root.textContent ?? '';
    expect(text).toContain('clean.png');
    expect(text).toContain('pending.png');
    expect(text).toContain('PENDING scan — cannot be selected yet');
    expect(text).toContain('failed.png');
    expect(text).toContain('FAILED — cannot be selected');
    // Already frozen evidence and another Visit's evidence are not offered.
    expect(text).not.toContain('frozen.png');
    expect(text).not.toContain('elsewhere.png');

    const boxes = checkboxes(root);
    expect(boxes).toHaveLength(3);
    expect(boxes.map((box) => box.disabled)).toEqual([false, true, true]);
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(true);
  });

  it('requires 1 to 10 selected CLEAN items before the summary can be sent', () => {
    const { fixture, root } = load(checkedOut);
    const twelve = Array.from({ length: 12 }, (_, i) => evidenceItem(`e${i + 1}`));
    openSummaryDialogAndType(fixture, root, 'Replaced the pump seal.', 'REPAIRED', twelve, false);

    // Nothing is selected for the Technician: no automatic selection.
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(true);
    expect(root.textContent).toContain('0 of 10 selected.');

    toggleEvidence(fixture, root, 0);
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(false);

    for (let index = 1; index < 10; index++) {
      toggleEvidence(fixture, root, index);
    }
    expect(root.textContent).toContain('10 of 10 selected.');
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(false);
    // The eleventh and twelfth cannot be selected any more; the selected ones can still be unselected.
    expect(checkboxes(root).map((box) => box.disabled)).toEqual([...Array<boolean>(10).fill(false), true, true]);

    toggleEvidence(fixture, root, 11); // a disabled box: no effect
    expect(root.textContent).toContain('10 of 10 selected.');

    for (let index = 0; index < 10; index++) {
      toggleEvidence(fixture, root, index);
    }
    expect(root.textContent).toContain('0 of 10 selected.');
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(true);
  });

  it('sends exactly the selected evidenceIds in selection order, never an unselected or non-CLEAN id', () => {
    const { fixture, root } = load(checkedOut);
    openSummaryDialogAndType(
      fixture,
      root,
      'Replaced the pump seal.',
      'REPAIRED',
      [evidenceItem('a'), evidenceItem('pending', { scanStatus: 'PENDING' }), evidenceItem('b'), evidenceItem('c')],
      false,
    );

    toggleEvidence(fixture, root, 3); // c
    toggleEvidence(fixture, root, 0); // a
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    const req = httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/submit-work-summary`);
    expect(req.request.body).toEqual({ summaryText: 'Replaced the pump seal.', repairOutcomeCode: 'REPAIRED', evidenceIds: ['c', 'a'] });
    req.flush({});
    httpMock.expectOne(`${baseUrl}/current`).flush(null, { status: 204, statusText: 'No Content' });
  });

  it('sends one Submit only while a request is in flight, and ignores a second submit event', () => {
    const { fixture, root } = load(checkedOut);
    openSummaryDialogAndType(fixture, root, 'Replaced the pump seal.', 'REPAIRED');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(true);

    const req = httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/submit-work-summary`);
    req.flush({});
    httpMock.expectOne(`${baseUrl}/current`).flush(null, { status: 204, statusText: 'No Content' });
  });

  it('refreshes the evidence list on request: a PENDING item that became CLEAN becomes selectable, a vanished selection is dropped', () => {
    const { fixture, root } = load(checkedOut);
    openSummaryDialogAndType(
      fixture,
      root,
      'Replaced the pump seal.',
      'REPAIRED',
      [evidenceItem('a'), evidenceItem('p', { scanStatus: 'PENDING' })],
      true,
    );
    expect(root.textContent).toContain('1 of 10 selected.');

    buttonNamed(root, 'Refresh status')!.click();
    fixture.detectChanges();
    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/evidence`).flush({ items: [evidenceItem('a'), evidenceItem('p')] });
    fixture.detectChanges();

    expect(checkboxes(root).map((box) => box.disabled)).toEqual([false, false]);
    expect(root.textContent).toContain('1 of 10 selected.');

    // The selected item is frozen elsewhere: it is no longer offered and the selection is cleared.
    buttonNamed(root, 'Refresh status')!.click();
    fixture.detectChanges();
    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/evidence`).flush({ items: [evidenceItem('a', { bound: true }), evidenceItem('p')] });
    fixture.detectChanges();

    expect(checkboxes(root)).toHaveLength(1);
    expect(root.textContent).toContain('0 of 10 selected.');
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(true);
  });

  it('says so when the Visit has no evidence yet and keeps Confirm disabled', () => {
    const { fixture, root } = load(checkedOut);

    openSummaryDialogAndType(fixture, root, 'Replaced the pump seal.', 'REPAIRED', []);

    expect(root.textContent).toContain('No evidence has been uploaded for this visit yet.');
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(true);
  });

  it('shows a plain message when the evidence list cannot be loaded, without selecting anything', () => {
    const { fixture, root } = load(checkedOut);
    buttonNamed(root, 'Submit Work Summary')!.click();
    fixture.detectChanges();

    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/evidence`).flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root.textContent).toContain('The evidence list could not be loaded.');
    expect(root.textContent).not.toContain('Http failure');
    expect(buttonNamed(root, 'Confirm Submit')!.disabled).toBe(true);
  });

  it('submits with the trimmed summary, the selected outcome code, and the quoted workOrderRowVersion, then reloads the current session', () => {
    const { fixture, root } = load(checkedOut);
    // The outcome comes from a closed dropdown (never free text), so only the summary text needs trimming.
    openSummaryDialogAndType(fixture, root, '  Replaced the pump seal.  ', 'REPAIRED');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    const req = httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/submit-work-summary`);
    expect(req.request.headers.get('If-Match')).toBe('"wov1"');
    expect(req.request.body).toEqual({ summaryText: 'Replaced the pump seal.', repairOutcomeCode: 'REPAIRED', evidenceIds: ['e1'] });
    req.flush({});

    httpMock.expectOne(`${baseUrl}/current`).flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).toBeNull();
    expect(root.textContent?.trim()).toBe('');
  });

  it('keeps the dialog open with a plain message when the backend rejects invalid fields (422)', () => {
    const { fixture, root } = load(checkedOut);
    openSummaryDialogAndType(fixture, root, 'x', 'REPAIRED');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock
      .expectOne(`${workOrdersBaseUrl}/wo-1/submit-work-summary`)
      .flush({ code: 'VALIDATION_FAILED', errors: { summaryText: ['required'] } }, { status: 422, statusText: 'Unprocessable Entity' });
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).not.toBeNull();
    expect(root.textContent).toContain('Please fill in both the summary and the repair outcome code.');
  });

  it('shows the server message and reloads when Submit Work Summary returns a STATE_CONFLICT', () => {
    const { fixture, root } = load(checkedOut);
    openSummaryDialogAndType(fixture, root, 'x', 'REPAIRED');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock
      .expectOne(`${workOrdersBaseUrl}/wo-1/submit-work-summary`)
      .flush({ code: 'STATE_CONFLICT', detail: "This Work Order's Work Summary was already submitted." }, { status: 409, statusText: 'Conflict' });
    httpMock.expectOne(`${baseUrl}/current`).flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).toBeNull();
    expect(root.textContent).toContain("This Work Order's Work Summary was already submitted.");
  });

  it('shows a generic message, not the raw HTTP error, for a Submit Work Summary server error', () => {
    const { fixture, root } = load(checkedOut);
    openSummaryDialogAndType(fixture, root, 'x', 'REPAIRED');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/submit-work-summary`).flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).not.toBeNull();
    expect(root.textContent).toContain('Something went wrong');
    expect(root.textContent).not.toContain('Http failure');
  });

  it('closes the Submit Work Summary dialog without a request when cancelled', () => {
    const { fixture, root } = load(checkedOut);
    openSummaryDialogAndType(fixture, root, 'x', 'REPAIRED');

    buttonNamed(root, 'Cancel')!.click();
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).toBeNull();
    expect(buttonNamed(root, 'Submit Work Summary')).toBeDefined();
  });

  // ---------------- Evidence (WEV-API-001/002) and the CLEAN guard message ----------------

  it('does not load or show evidence until the Technician asks for it, and links to the Service Report', () => {
    const { root } = load(checkedIn);

    expect(root.querySelector('app-work-evidence-panel')).toBeNull();
    httpMock.expectNone(`${workOrdersBaseUrl}/wo-1/evidence`);
    expect(root.querySelector('a')?.textContent?.trim()).toBe('Service Report');
    expect(root.querySelector('a')?.getAttribute('href')).toBe('/work-orders/wo-1/service-report');
  });

  it.each([
    ['CHECKED_IN', checkedIn],
    ['PAUSED', paused],
    ['CHECKED_OUT', checkedOut],
  ])('opens the evidence panel for a %s session and loads its list once', (_name, session) => {
    const { fixture, root } = load(session);

    buttonNamed(root, 'Evidence')!.click();
    fixture.detectChanges();
    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/evidence`).flush({ items: [] });
    fixture.detectChanges();

    expect(root.querySelector('app-work-evidence-panel')).not.toBeNull();
    expect(buttonNamed(root, 'Hide evidence')).toBeDefined();
    expect(root.textContent).toContain('No evidence has been uploaded yet.');
  });

  it('explains an invalid evidence selection (422 on the evidenceIds field) instead of blaming the form fields', () => {
    const { fixture, root } = load(checkedOut);
    openSummaryDialogAndType(fixture, root, 'Replaced the bearing', 'REPAIRED');

    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/submit-work-summary`).flush(
      { code: 'VALIDATION_FAILED', errors: { evidenceIds: ['The selected evidence is not valid for this Work Summary. Select only CLEAN evidence uploaded for the current Visit.'] } },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    fixture.detectChanges();

    expect(root.querySelector('[role="dialog"]')).not.toBeNull();
    expect(root.querySelector('[role="alert"]')?.textContent).toContain('The selected evidence is not valid for this Work Summary.');
    expect(root.textContent).not.toContain('Please fill in both the summary and the repair outcome code.');
  });
});
