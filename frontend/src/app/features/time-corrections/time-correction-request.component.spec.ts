import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { TimeCorrection, TimeCorrectionTargets } from './time-correction.models';
import { TimeCorrectionRequestComponent } from './time-correction-request.component';

describe('TimeCorrectionRequestComponent', () => {
  let httpMock: HttpTestingController;
  const apiUrl = `${environment.apiBaseUrl}/v1`;
  const targetsUrl = `${apiUrl}/work-orders/wo-1/time-correction-targets`;
  const requestUrl = `${apiUrl}/work-sessions/session-1/time-corrections`;

  function targets(overrides: Partial<TimeCorrectionTargets> = {}): TimeCorrectionTargets {
    return {
      workOrderId: 'wo-1',
      workOrderNo: 'WO-1',
      workOrderStatus: 'IN_PROGRESS',
      siteCode: 'SITE-A',
      equipmentCode: 'EQ-1',
      sessions: [
        {
          workSessionId: 'session-1',
          serviceVisitId: 'visit-1',
          status: 'COMPLETED',
          checkInAt: '2026-10-14T08:00:00Z',
          checkOutAt: '2026-10-14T12:00:00Z',
          pauses: [
            { workSessionPauseId: 'pause-1', pausedAt: '2026-10-14T09:00:00Z', resumedAt: '2026-10-14T09:30:00Z' },
            { workSessionPauseId: 'pause-2', pausedAt: '2026-10-14T10:00:00Z', resumedAt: '2026-10-14T10:20:00Z' },
            { workSessionPauseId: 'pause-3', pausedAt: '2026-10-14T11:00:00Z', resumedAt: null },
          ],
        },
      ],
      ...overrides,
    };
  }

  const created: TimeCorrection = {
    timeCorrectionId: 'tc-1',
    workSessionId: 'session-1',
    workSessionPauseId: null,
    serviceVisitId: 'visit-1',
    workOrderId: 'wo-1',
    workOrderNo: 'WO-1',
    siteCode: 'SITE-A',
    equipmentCode: 'EQ-1',
    timestampType: 'CHECK_IN',
    oldValue: '2026-10-14T08:00:00Z',
    newValue: '2026-10-14T07:45:00Z',
    reason: 'Forgot to check in.',
    requestedBy: 'user-1',
    status: 'PENDING',
    decidedBy: null,
    decisionReason: null,
    decidedAt: null,
    rowVersion: 'v1',
  };

  function createComponent(): ComponentFixture<TimeCorrectionRequestComponent> {
    TestBed.configureTestingModule({
      imports: [TimeCorrectionRequestComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(TimeCorrectionRequestComponent);
    fixture.componentRef.setInput('workOrderId', 'wo-1');
    return fixture;
  }

  /** Creates the component and answers the targets read with `loaded`. */
  async function loaded(loadedTargets: TimeCorrectionTargets = targets()): Promise<ComponentFixture<TimeCorrectionRequestComponent>> {
    const fixture = createComponent();
    fixture.detectChanges();
    httpMock.expectOne(targetsUrl).flush(loadedTargets);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  function el(fixture: ComponentFixture<unknown>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function selects(fixture: ComponentFixture<unknown>): HTMLSelectElement[] {
    return Array.from(el(fixture).querySelectorAll('select'));
  }

  const sessionSelect = (f: ComponentFixture<unknown>) => selects(f)[0];
  const typeSelect = (f: ComponentFixture<unknown>) => selects(f)[1];
  const pauseSelect = (f: ComponentFixture<unknown>) => selects(f)[2];

  function choose(fixture: ComponentFixture<unknown>, select: HTMLSelectElement, value: string): void {
    select.value = value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  function type(fixture: ComponentFixture<unknown>, field: HTMLInputElement | HTMLTextAreaElement, value: string): void {
    field.value = value;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  const newValueInput = (f: ComponentFixture<unknown>) => el(f).querySelector('input[type="datetime-local"]') as HTMLInputElement;
  const reasonInput = (f: ComponentFixture<unknown>) => el(f).querySelector('textarea') as HTMLTextAreaElement;
  const submitButton = (f: ComponentFixture<unknown>) => el(f).querySelector('button[type="submit"]') as HTMLButtonElement;
  const text = (f: ComponentFixture<unknown>) => el(f).textContent ?? '';

  async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** Fills type, optional pause, new value and reason, leaving the form ready to submit. */
  function fill(
    fixture: ComponentFixture<unknown>,
    options: { type: string; pause?: string; newValue: string; reason?: string },
  ): void {
    choose(fixture, typeSelect(fixture), options.type);
    if (options.pause !== undefined) {
      choose(fixture, pauseSelect(fixture), options.pause);
    }
    type(fixture, newValueInput(fixture), options.newValue);
    type(fixture, reasonInput(fixture), options.reason ?? 'Corrected from the paper log.');
  }

  function submit(fixture: ComponentFixture<unknown>): TestRequest {
    submitButton(fixture).click();
    fixture.detectChanges();
    return httpMock.expectOne(requestUrl);
  }

  afterEach(() => httpMock.verify());

  describe('loading', () => {
    it('shows a loading state until the targets arrive and reads the Work Order it is given', () => {
      const fixture = createComponent();
      fixture.detectChanges();

      expect(text(fixture)).toContain('Loading work sessions');
      httpMock.expectOne(targetsUrl).flush(targets());
    });

    it('selects the only session by itself and shows the Work Order, its status and the recorded times in UTC', async () => {
      const fixture = await loaded();

      expect(text(fixture)).toContain('Work Order WO-1');
      expect(text(fixture)).toContain('IN_PROGRESS');
      expect(text(fixture)).toContain('2026-10-14 08:00:00 UTC');
      expect(text(fixture)).toContain('2026-10-14 12:00:00 UTC');
      expect(sessionSelect(fixture).value).toBe('session-1');
    });

    it('does not pick for the user when there are several sessions', async () => {
      const two = targets();
      two.sessions.push({ ...two.sessions[0], workSessionId: 'session-2', checkInAt: '2026-10-15T08:00:00Z' });
      const fixture = await loaded(two);

      expect(sessionSelect(fixture).value).toBe('');
      expect(typeSelect(fixture).disabled).toBe(true);

      choose(fixture, sessionSelect(fixture), 'session-2');
      expect(typeSelect(fixture).disabled).toBe(false);
    });

    it('tells sessions apart even when they share a check-in time (a Team Lead sees every session of the Work Order)', async () => {
      const two = targets();
      two.sessions.push({ ...two.sessions[0], workSessionId: 'session-2', pauses: [] });
      const fixture = await loaded(two);

      const labels = Array.from(sessionSelect(fixture).options)
        .filter((option) => option.value !== '')
        .map((option) => option.textContent?.replace(/\s+/g, ' ').trim());

      expect(labels).toEqual([
        'Session 1 — checked in 2026-10-14 08:00:00 UTC — COMPLETED — 3 pauses',
        'Session 2 — checked in 2026-10-14 08:00:00 UTC — COMPLETED — 0 pauses',
      ]);
    });

    it.each([
      [401, 'not signed in'],
      [403, 'cannot request time corrections'],
      [404, 'No work session of this Work Order is available'],
    ])('shows a clear state for a %i without offering a form', async (status, message) => {
      const fixture = createComponent();
      fixture.detectChanges();
      httpMock.expectOne(targetsUrl).flush({}, { status, statusText: 'x' });
      await settle(fixture);

      expect(text(fixture)).toContain(message);
      expect(el(fixture).querySelector('form')).toBeNull();
    });

    it('offers a retry after a failure and loads again only when asked', async () => {
      const fixture = createComponent();
      fixture.detectChanges();
      httpMock.expectOne(targetsUrl).flush({}, { status: 500, statusText: 'Server Error' });
      await settle(fixture);

      expect(text(fixture)).toContain('could not be loaded');
      httpMock.expectNone(targetsUrl); // nothing retried by itself

      (Array.from(el(fixture).querySelectorAll('button')).find((b) => b.textContent?.includes('Try again')) as HTMLButtonElement).click();
      httpMock.expectOne(targetsUrl).flush(targets());
      await settle(fixture);

      expect(el(fixture).querySelector('form')).not.toBeNull();
    });
  });

  describe('type and pause selection', () => {
    it('offers the four types and marks the ones this session cannot correct', async () => {
      const open = targets();
      open.sessions[0] = { ...open.sessions[0], status: 'IN_PROGRESS', checkOutAt: null, pauses: [] };
      const fixture = await loaded(open);

      const options = Array.from(typeSelect(fixture).options).filter((o) => o.value !== '');
      expect(options.map((o) => o.value)).toEqual(['CHECK_IN', 'PAUSE_START', 'RESUME', 'CHECK_OUT']);
      expect(options.map((o) => o.disabled)).toEqual([false, true, true, true]);
      expect(options[1].textContent).toContain('the session has no pause');
      expect(options[3].textContent).toContain('not checked out yet');
    });

    it('does not show a pause list for CHECK_IN or CHECK_OUT', async () => {
      const fixture = await loaded();

      choose(fixture, typeSelect(fixture), 'CHECK_IN');
      expect(selects(fixture).length).toBe(2);
      choose(fixture, typeSelect(fixture), 'CHECK_OUT');
      expect(selects(fixture).length).toBe(2);
    });

    it('lists every pause for PAUSE_START but only resumed pauses for RESUME', async () => {
      const fixture = await loaded();

      choose(fixture, typeSelect(fixture), 'PAUSE_START');
      expect(Array.from(pauseSelect(fixture).options).map((o) => o.value)).toEqual(['', 'pause-1', 'pause-2', 'pause-3']);

      choose(fixture, typeSelect(fixture), 'RESUME');
      expect(Array.from(pauseSelect(fixture).options).map((o) => o.value)).toEqual(['', 'pause-1', 'pause-2']);
    });

    it('shows the current value of the exact pause chosen', async () => {
      const fixture = await loaded();

      choose(fixture, typeSelect(fixture), 'PAUSE_START');
      choose(fixture, pauseSelect(fixture), 'pause-2');

      expect(text(fixture)).toContain('Current pause start time: 2026-10-14 10:00:00 UTC');
      expect(newValueInput(fixture).value).toMatch(/^2026-10-14T10:00(:00)?$/); // the input drops zero seconds

      choose(fixture, typeSelect(fixture), 'RESUME');
      choose(fixture, pauseSelect(fixture), 'pause-2');
      expect(text(fixture)).toContain('Current resume time: 2026-10-14 10:20:00 UTC');
    });
  });

  describe('sending a request', () => {
    it('CHECK_IN: sends the new time as an explicit UTC instant, the trimmed reason and no pause id', async () => {
      const fixture = await loaded();
      fill(fixture, { type: 'CHECK_IN', newValue: '2026-10-14T07:45', reason: '  Forgot to check in.  ' });

      expect(submitButton(fixture).disabled).toBe(false);
      const req = submit(fixture);

      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({
        timestampType: 'CHECK_IN',
        newValue: '2026-10-14T07:45:00.000Z',
        reason: 'Forgot to check in.',
      });
      req.flush(created);
      httpMock.expectOne(targetsUrl).flush(targets());
      await settle(fixture);
    });

    it('PAUSE_START: names the exact pause chosen, not the latest one', async () => {
      const fixture = await loaded();
      fill(fixture, { type: 'PAUSE_START', pause: 'pause-1', newValue: '2026-10-14T08:55:00' });

      const req = submit(fixture);

      expect(req.request.body).toEqual({
        timestampType: 'PAUSE_START',
        workSessionPauseId: 'pause-1',
        newValue: '2026-10-14T08:55:00.000Z',
        reason: 'Corrected from the paper log.',
      });
      req.flush({ ...created, timestampType: 'PAUSE_START', workSessionPauseId: 'pause-1' });
      httpMock.expectOne(targetsUrl).flush(targets());
      await settle(fixture);
    });

    it('RESUME: names the exact resumed pause', async () => {
      const fixture = await loaded();
      fill(fixture, { type: 'RESUME', pause: 'pause-2', newValue: '2026-10-14T10:25:00' });

      const req = submit(fixture);

      expect(req.request.body.timestampType).toBe('RESUME');
      expect(req.request.body.workSessionPauseId).toBe('pause-2');
      expect(req.request.body.newValue).toBe('2026-10-14T10:25:00.000Z');
      req.flush({ ...created, timestampType: 'RESUME', workSessionPauseId: 'pause-2' });
      httpMock.expectOne(targetsUrl).flush(targets());
      await settle(fixture);
    });

    it('CHECK_OUT of a completed session is allowed and sends no pause id', async () => {
      const fixture = await loaded();
      fill(fixture, { type: 'CHECK_OUT', newValue: '2026-10-14T12:10:00' });

      const req = submit(fixture);

      expect(req.request.body).toEqual({
        timestampType: 'CHECK_OUT',
        newValue: '2026-10-14T12:10:00.000Z',
        reason: 'Corrected from the paper log.',
      });
      req.flush({ ...created, timestampType: 'CHECK_OUT' });
      httpMock.expectOne(targetsUrl).flush(targets());
      await settle(fixture);
    });

    it('never sends the old value, status, requester or tenant', async () => {
      const fixture = await loaded();
      fill(fixture, { type: 'CHECK_IN', newValue: '2026-10-14T07:45' });

      const req = submit(fixture);

      expect(Object.keys(req.request.body).sort()).toEqual(['newValue', 'reason', 'timestampType']);
      req.flush(created);
      httpMock.expectOne(targetsUrl).flush(targets());
      await settle(fixture);
    });

    it('on success shows the confirmation, emits the correction, clears the form and reloads the sessions', async () => {
      const fixture = await loaded();
      const emitted: TimeCorrection[] = [];
      fixture.componentInstance.requested.subscribe((value) => emitted.push(value));
      fill(fixture, { type: 'CHECK_IN', newValue: '2026-10-14T07:45' });

      submit(fixture).flush(created);
      httpMock.expectOne(targetsUrl).flush(targets());
      await settle(fixture);

      expect(emitted).toEqual([created]);
      expect(text(fixture)).toContain('Request sent for Supervisor review');
      expect(text(fixture)).toContain('Nothing changes until a Supervisor approves it');
      expect(reasonInput(fixture).value).toBe('');
      expect(submitButton(fixture).disabled).toBe(true);
    });

    it('sends one POST for a double click and disables the form while it is in flight', async () => {
      const fixture = await loaded();
      fill(fixture, { type: 'CHECK_IN', newValue: '2026-10-14T07:45' });

      submitButton(fixture).click();
      submitButton(fixture).click();
      fixture.detectChanges();

      const req = httpMock.expectOne(requestUrl); // exactly one
      expect(submitButton(fixture).disabled).toBe(true);
      expect(reasonInput(fixture).disabled).toBe(true);
      req.flush(created);
      httpMock.expectOne(targetsUrl).flush(targets());
      await settle(fixture);
    });
  });

  describe('validation before sending', () => {
    it('keeps Send disabled until type, new time and reason are valid', async () => {
      const fixture = await loaded();
      expect(submitButton(fixture).disabled).toBe(true);

      choose(fixture, typeSelect(fixture), 'CHECK_IN');
      type(fixture, newValueInput(fixture), '2026-10-14T07:45');
      expect(submitButton(fixture).disabled).toBe(true); // no reason yet

      type(fixture, reasonInput(fixture), 'Because.');
      expect(submitButton(fixture).disabled).toBe(false);
    });

    it('refuses a new time equal to the current one (no-op)', async () => {
      const fixture = await loaded();
      choose(fixture, typeSelect(fixture), 'CHECK_IN');
      type(fixture, newValueInput(fixture), '2026-10-14T08:00:00');
      type(fixture, reasonInput(fixture), 'Because.');

      expect(submitButton(fixture).disabled).toBe(true);
      expect(text(fixture)).toContain('same as the current time');
    });

    it('refuses a new time in the future', async () => {
      const fixture = await loaded();
      fill(fixture, { type: 'CHECK_IN', newValue: '2999-01-01T00:00' });

      expect(submitButton(fixture).disabled).toBe(true);
      expect(text(fixture)).toContain('cannot be in the future');
    });

    it('refuses a blank or too-long reason', async () => {
      const fixture = await loaded();
      fill(fixture, { type: 'CHECK_IN', newValue: '2026-10-14T07:45', reason: '   ' });
      expect(submitButton(fixture).disabled).toBe(true);
      expect(text(fixture)).toContain('A reason is required');

      type(fixture, reasonInput(fixture), 'x'.repeat(1001));
      expect(submitButton(fixture).disabled).toBe(true);
      expect(text(fixture)).toContain('at most 1000 characters');
    });

    it('requires the pause for a pause-type correction', async () => {
      const fixture = await loaded();
      choose(fixture, typeSelect(fixture), 'PAUSE_START');
      type(fixture, newValueInput(fixture), '2026-10-14T08:55');
      type(fixture, reasonInput(fixture), 'Because.');

      expect(submitButton(fixture).disabled).toBe(true);
    });

    it('does not let a Send go out from a disabled form even when the submit event fires', async () => {
      const fixture = await loaded();

      el(fixture).querySelector('form')!.dispatchEvent(new Event('submit'));
      fixture.detectChanges();

      httpMock.expectNone(requestUrl);
    });
  });

  describe('terminal Work Orders', () => {
    it.each(['CLOSED', 'CANCELLED'])('explains and disables the form for a %s Work Order', async (status) => {
      const fixture = await loaded(targets({ workOrderStatus: status }));

      expect(text(fixture)).toContain(`cannot be requested for a ${status} Work Order`);
      expect(text(fixture)).toContain('can still be rejected by the Supervisor');
      expect(sessionSelect(fixture).disabled).toBe(true);
      expect(reasonInput(fixture).disabled).toBe(true);
      expect(submitButton(fixture).disabled).toBe(true);
    });
  });

  describe('backend answers', () => {
    async function submitted(): Promise<{ fixture: ComponentFixture<TimeCorrectionRequestComponent>; req: TestRequest }> {
      const fixture = await loaded();
      fill(fixture, { type: 'PAUSE_START', pause: 'pause-1', newValue: '2026-10-14T08:55' });
      return { fixture, req: submit(fixture) };
    }

    it('puts 422 field errors on their fields and keeps what was typed', async () => {
      const { fixture, req } = await submitted();
      req.flush(
        {
          code: 'VALIDATION_FAILED',
          errors: { newValue: ['New value must not be before the check-in time.'], workSessionPauseId: ['Unknown pause.'] },
        },
        { status: 422, statusText: 'Unprocessable' },
      );
      await settle(fixture);

      expect(text(fixture)).toContain('New value must not be before the check-in time.');
      expect(text(fixture)).toContain('Unknown pause.');
      expect(reasonInput(fixture).value).toBe('Corrected from the paper log.');
      expect(newValueInput(fixture).getAttribute('aria-invalid')).toBe('true');
    });

    it('shows the detail of a STATE_CONFLICT 409 and reloads the sessions without retrying the POST', async () => {
      const { fixture, req } = await submitted();
      req.flush(
        { code: 'STATE_CONFLICT', detail: 'A time correction for this timestamp is already pending.' },
        { status: 409, statusText: 'Conflict' },
      );
      httpMock.expectOne(targetsUrl).flush(targets());
      await settle(fixture);

      expect(text(fixture)).toContain('already pending');
      httpMock.expectNone(requestUrl);
    });

    it('explains a CONCURRENCY_CONFLICT 409 generically', async () => {
      const { fixture, req } = await submitted();
      req.flush({ code: 'CONCURRENCY_CONFLICT' }, { status: 409, statusText: 'Conflict' });
      httpMock.expectOne(targetsUrl).flush(targets());
      await settle(fixture);

      expect(text(fixture)).toContain('changed by another request');
    });

    it('404: says the session is gone or out of scope, reloads the sessions and does not retry', async () => {
      const { fixture, req } = await submitted();
      req.flush({}, { status: 404, statusText: 'Not Found' });
      httpMock.expectOne(targetsUrl).flush(targets({ sessions: [] }));
      await settle(fixture);

      expect(text(fixture)).toContain('not found, or it is no longer in your scope');
      httpMock.expectNone(requestUrl);
    });

    it.each([
      [403, 'do not have permission'],
      [401, 'not signed in'],
      [0, 'Could not reach the server'],
      [500, 'Something went wrong'],
    ])('%i is shown plainly, the form stays editable and nothing is retried', async (status, message) => {
      const { fixture, req } = await submitted();
      req.flush({}, { status, statusText: 'x' });
      await settle(fixture);

      expect(text(fixture)).toContain(message);
      expect(reasonInput(fixture).disabled).toBe(false);
      httpMock.expectNone(requestUrl);
    });
  });
});
