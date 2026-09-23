import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { ActiveWorkSessionComponent } from './active-work-session.component';
import { WorkSession } from './work-session.models';

describe('ActiveWorkSessionComponent', () => {
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/work-sessions`;

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
      providers: [provideHttpClient(), provideHttpClientTesting()],
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

  it('shows no buttons at all for a CHECKED_OUT session', () => {
    const { root } = load(checkedOut);

    expect(root.textContent).toContain('CHECKED_OUT');
    expect(buttonNamed(root, 'Pause')).toBeUndefined();
    expect(buttonNamed(root, 'Resume')).toBeUndefined();
    expect(buttonNamed(root, 'Check-out')).toBeUndefined();
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
});
