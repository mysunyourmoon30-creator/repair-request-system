import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { PagedTimeCorrections, TimeCorrection } from './time-correction.models';
import { TimeCorrectionReviewComponent } from './time-correction-review.component';

function tokenWithPayload(payload: Record<string, unknown>): string {
  const encode = (value: unknown) =>
    btoa(JSON.stringify(value)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${encode({ alg: 'none' })}.${encode(payload)}.signature`;
}

describe('TimeCorrectionReviewComponent', () => {
  let httpMock: HttpTestingController;
  const apiUrl = `${environment.apiBaseUrl}/v1`;
  const listUrl = `${apiUrl}/time-corrections`;

  function item(overrides: Partial<TimeCorrection> = {}): TimeCorrection {
    return {
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
      requestedBy: 'technician-1-0000',
      status: 'PENDING',
      decidedBy: null,
      decisionReason: null,
      decidedAt: null,
      rowVersion: 'v1',
      ...overrides,
    };
  }

  function paged(items: TimeCorrection[], page = 1, totalCount = items.length): PagedTimeCorrections {
    return { items, page, pageSize: 20, totalCount };
  }

  function createComponent(): ComponentFixture<TimeCorrectionReviewComponent> {
    TestBed.configureTestingModule({
      imports: [TimeCorrectionReviewComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(TimeCorrectionReviewComponent);
  }

  function expectList(page = 1): TestRequest {
    return httpMock.expectOne((request) => request.url === listUrl && request.params.get('page') === String(page));
  }

  async function loaded(
    result: PagedTimeCorrections = paged([item()]),
  ): Promise<ComponentFixture<TimeCorrectionReviewComponent>> {
    const fixture = createComponent();
    fixture.detectChanges();
    expectList().flush(result);
    await settle(fixture);
    return fixture;
  }

  async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const el = (f: ComponentFixture<unknown>) => f.nativeElement as HTMLElement;
  const text = (f: ComponentFixture<unknown>) => el(f).textContent ?? '';
  const button = (f: ComponentFixture<unknown>, label: string) =>
    Array.from(el(f).querySelectorAll('button')).find((b) => b.textContent?.trim() === label) as HTMLButtonElement;
  const rejectReason = (f: ComponentFixture<unknown>) => el(f).querySelector('textarea') as HTMLTextAreaElement;

  function typeReason(fixture: ComponentFixture<unknown>, value: string): void {
    const field = rejectReason(fixture);
    field.value = value;
    field.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  describe('loading', () => {
    it('shows loading, then the pending request with everything the Supervisor needs to decide', async () => {
      const fixture = createComponent();
      fixture.detectChanges();
      expect(text(fixture)).toContain('Loading');
      expectList().flush(paged([item()]));
      await settle(fixture);

      expect(text(fixture)).toContain('WO-1');
      expect(text(fixture)).toContain('Check-in time');
      expect(text(fixture)).toContain('SITE-A');
      expect(text(fixture)).toContain('EQ-1');
      expect(text(fixture)).toContain('2026-10-14 08:00:00 UTC'); // current
      expect(text(fixture)).toContain('2026-10-14 07:45:00 UTC'); // requested
      expect(text(fixture)).toContain('Forgot to check in.'); // review-screen disclosure follows TIME-API-ADD-001
      expect(button(fixture, 'Approve')).toBeTruthy();
      expect(button(fixture, 'Reject')).toBeTruthy();
    });

    it('asks the server for the first page of 20', async () => {
      const fixture = createComponent();
      fixture.detectChanges();

      const req = expectList();
      expect(req.request.params.get('pageSize')).toBe('20');
      req.flush(paged([]));
      await settle(fixture);
    });

    it('says so when nothing is awaiting review and offers no decisions', async () => {
      const fixture = await loaded(paged([]));

      expect(text(fixture)).toContain('No time corrections are awaiting review');
      expect(button(fixture, 'Approve')).toBeUndefined();
    });

    it('names the pause of a pause-type request', async () => {
      const fixture = await loaded(
        paged([item({ timestampType: 'RESUME', workSessionPauseId: 'abcdef12-0000-0000-0000-000000000000' })]),
      );

      expect(text(fixture)).toContain('Resume time');
      expect(text(fixture)).toContain('abcdef12');
    });

    it.each([
      [401, 'not signed in'],
      [403, 'cannot review time corrections'],
    ])('shows a clear state for a %i', async (status, message) => {
      const fixture = createComponent();
      fixture.detectChanges();
      expectList().flush({}, { status, statusText: 'x' });
      await settle(fixture);

      expect(text(fixture)).toContain(message);
      expect(button(fixture, 'Approve')).toBeUndefined();
    });

    it('offers a retry after a failure, and only loads again when asked', async () => {
      const fixture = createComponent();
      fixture.detectChanges();
      expectList().flush({}, { status: 500, statusText: 'x' });
      await settle(fixture);

      expect(text(fixture)).toContain('could not be loaded');
      httpMock.expectNone((request) => request.url === listUrl);

      button(fixture, 'Try again').click();
      expectList().flush(paged([item()]));
      await settle(fixture);

      expect(text(fixture)).toContain('WO-1');
    });
  });

  describe('approve', () => {
    it("sends the item's own row version as a quoted If-Match, then confirms and reloads the list", async () => {
      const fixture = await loaded(paged([item({ rowVersion: 'row-7' })]));

      button(fixture, 'Approve').click();
      fixture.detectChanges();
      const req = httpMock.expectOne(`${listUrl}/tc-1/approve`);

      expect(req.request.method).toBe('POST');
      expect(req.request.headers.get('If-Match')).toBe('"row-7"');
      req.flush(item({ status: 'APPROVED', rowVersion: 'row-8' }));
      expectList().flush(paged([]));
      await settle(fixture);

      expect(text(fixture)).toContain('Approved: check-in time of WO-1');
      expect(text(fixture)).toContain('No time corrections are awaiting review');
    });

    it('sends one POST for a double click and disables every decision while it is in flight', async () => {
      const fixture = await loaded(paged([item(), item({ timeCorrectionId: 'tc-2', rowVersion: 'v2' })]));

      const approve = button(fixture, 'Approve');
      approve.click();
      approve.click();
      fixture.detectChanges();

      const req = httpMock.expectOne(`${listUrl}/tc-1/approve`);
      Array.from(el(fixture).querySelectorAll('button'))
        .filter((b) => ['Approve', 'Reject'].includes(b.textContent?.trim() ?? ''))
        .forEach((b) => expect(b.disabled).toBe(true));
      req.flush(item({ status: 'APPROVED' }));
      expectList().flush(paged([item({ timeCorrectionId: 'tc-2', rowVersion: 'v2' })]));
      await settle(fixture);
    });

    it('409 CONCURRENCY_CONFLICT: says it changed, reloads the list and does NOT decide again by itself', async () => {
      const fixture = await loaded();

      button(fixture, 'Approve').click();
      httpMock
        .expectOne(`${listUrl}/tc-1/approve`)
        .flush({ code: 'CONCURRENCY_CONFLICT' }, { status: 409, statusText: 'Conflict' });
      expectList().flush(paged([item({ rowVersion: 'v2' })]));
      await settle(fixture);

      expect(text(fixture)).toContain('changed by someone else');
      httpMock.expectNone(`${listUrl}/tc-1/approve`); // no automatic retry, not even with the fresh token
      expect(button(fixture, 'Approve').disabled).toBe(false);

      // A new, explicit decision uses the refreshed row version.
      button(fixture, 'Approve').click();
      expect(httpMock.expectOne(`${listUrl}/tc-1/approve`).request.headers.get('If-Match')).toBe('"v2"');
      httpMock.match(() => true).forEach((r) => r.flush(item({ status: 'APPROVED' })));
    });

    it('409 STATE_CONFLICT: shows the backend detail, points to Reject for a closed Work Order and reloads', async () => {
      const fixture = await loaded();

      button(fixture, 'Approve').click();
      httpMock
        .expectOne(`${listUrl}/tc-1/approve`)
        .flush({ code: 'STATE_CONFLICT', detail: 'The Work Order is CLOSED.' }, { status: 409, statusText: 'Conflict' });
      expectList().flush(paged([item()]));
      await settle(fixture);

      expect(text(fixture)).toContain('The Work Order is CLOSED.');
      expect(text(fixture)).toContain('you can still reject this request');
    });

    it('404: says the request is gone or out of scope and reloads', async () => {
      const fixture = await loaded();

      button(fixture, 'Approve').click();
      httpMock.expectOne(`${listUrl}/tc-1/approve`).flush({}, { status: 404, statusText: 'Not Found' });
      expectList().flush(paged([]));
      await settle(fixture);

      expect(text(fixture)).toContain('no longer exists, or it is outside your Site scope');
    });

    it('403: says the Supervisor cannot decide it and reloads', async () => {
      const fixture = await loaded();

      button(fixture, 'Approve').click();
      httpMock.expectOne(`${listUrl}/tc-1/approve`).flush({}, { status: 403, statusText: 'Forbidden' });
      expectList().flush(paged([item()]));
      await settle(fixture);

      expect(text(fixture)).toContain('do not have permission');
    });

    it('a network failure is shown, nothing is retried and the decision controls come back', async () => {
      const fixture = await loaded();

      button(fixture, 'Approve').click();
      httpMock.expectOne(`${listUrl}/tc-1/approve`).error(new ProgressEvent('error'));
      await settle(fixture);

      expect(text(fixture)).toContain('Could not reach the server');
      httpMock.expectNone(`${listUrl}/tc-1/approve`);
      expect(button(fixture, 'Approve').disabled).toBe(false);
    });
  });

  describe('reject', () => {
    async function opened(): Promise<ComponentFixture<TimeCorrectionReviewComponent>> {
      const fixture = await loaded(paged([item({ rowVersion: 'row-7' })]));
      button(fixture, 'Reject').click();
      fixture.detectChanges();
      return fixture;
    }

    it('asks for a reason first, and cannot be confirmed without one', async () => {
      const fixture = await opened();

      expect(rejectReason(fixture)).toBeTruthy();
      expect(button(fixture, 'Confirm Reject').disabled).toBe(true);
      typeReason(fixture, '   ');
      expect(button(fixture, 'Confirm Reject').disabled).toBe(true);

      el(fixture).querySelector('form')!.dispatchEvent(new Event('submit'));
      httpMock.expectNone(`${listUrl}/tc-1/reject`);
    });

    it('sends the reason as decisionReason with the quoted row version of the item', async () => {
      const fixture = await opened();
      typeReason(fixture, '  The visit log shows 08:00.  ');

      button(fixture, 'Confirm Reject').click();
      fixture.detectChanges();
      const req = httpMock.expectOne(`${listUrl}/tc-1/reject`);

      expect(req.request.headers.get('If-Match')).toBe('"row-7"');
      expect(req.request.body).toEqual({ decisionReason: 'The visit log shows 08:00.' });
      req.flush(item({ status: 'REJECTED' }));
      expectList().flush(paged([]));
      await settle(fixture);

      expect(text(fixture)).toContain('Rejected: check-in time of WO-1. No time was changed.');
      expect(rejectReason(fixture)).toBeNull();
    });

    it('cancel closes the form without a call', async () => {
      const fixture = await opened();

      button(fixture, 'Cancel').click();
      fixture.detectChanges();

      expect(rejectReason(fixture)).toBeNull();
      httpMock.expectNone(`${listUrl}/tc-1/reject`);
    });

    it('sends one POST for a double click on Confirm Reject', async () => {
      const fixture = await opened();
      typeReason(fixture, 'Not supported.');

      const confirm = button(fixture, 'Confirm Reject');
      confirm.click();
      confirm.click();
      fixture.detectChanges();

      const req = httpMock.expectOne(`${listUrl}/tc-1/reject`);
      req.flush(item({ status: 'REJECTED' }));
      expectList().flush(paged([]));
      await settle(fixture);
    });

    it('409: closes the reject form, reloads and requires a new explicit decision', async () => {
      const fixture = await opened();
      typeReason(fixture, 'Not supported.');

      button(fixture, 'Confirm Reject').click();
      httpMock
        .expectOne(`${listUrl}/tc-1/reject`)
        .flush({ code: 'CONCURRENCY_CONFLICT' }, { status: 409, statusText: 'Conflict' });
      expectList().flush(paged([item({ rowVersion: 'v2' })]));
      await settle(fixture);

      expect(text(fixture)).toContain('changed by someone else');
      expect(rejectReason(fixture)).toBeNull();
      httpMock.expectNone(`${listUrl}/tc-1/reject`);
    });

    it('a 422 keeps the form open with the reason message and does not reload', async () => {
      const fixture = await opened();
      typeReason(fixture, 'Not supported.');

      button(fixture, 'Confirm Reject').click();
      httpMock
        .expectOne(`${listUrl}/tc-1/reject`)
        .flush({ errors: { decisionReason: ['required'] } }, { status: 422, statusText: 'Unprocessable' });
      await settle(fixture);

      expect(text(fixture)).toContain('A decision reason is required.');
      expect(rejectReason(fixture)).toBeTruthy();
    });
  });

  describe('self-decision', () => {
    it('disables Approve and Reject for a request the signed-in user made, and explains why', async () => {
      localStorage.setItem('accessToken', tokenWithPayload({ sub: 'technician-1-0000', role: 'SUPERVISOR' }));
      const fixture = await loaded();

      expect(text(fixture)).toContain('you cannot approve or reject it');
      expect(button(fixture, 'Approve').disabled).toBe(true);
      expect(button(fixture, 'Reject').disabled).toBe(true);

      button(fixture, 'Approve').click();
      httpMock.expectNone(`${listUrl}/tc-1/approve`);
    });

    it("leaves other people's requests decidable", async () => {
      localStorage.setItem('accessToken', tokenWithPayload({ sub: 'supervisor-9', role: 'SUPERVISOR' }));
      const fixture = await loaded();

      expect(button(fixture, 'Approve').disabled).toBe(false);
      expect(button(fixture, 'Reject').disabled).toBe(false);
    });

    it("shows the backend's refusal if a self-decision is attempted anyway (403)", async () => {
      // The token carries no usable identity, so the UI cannot know; the backend is the real rule.
      const fixture = await loaded();

      button(fixture, 'Approve').click();
      httpMock.expectOne(`${listUrl}/tc-1/approve`).flush({}, { status: 403, statusText: 'Forbidden' });
      expectList().flush(paged([item()]));
      await settle(fixture);

      expect(text(fixture)).toContain('do not have permission');
    });
  });

  describe('paging', () => {
    it('shows the page position and moves to the next page', async () => {
      const fixture = await loaded(paged([item()], 1, 45));

      expect(text(fixture)).toContain('Page 1 of 3 (45 total)');
      expect(button(fixture, 'Previous').disabled).toBe(true);

      button(fixture, 'Next').click();
      expectList(2).flush(paged([item({ timeCorrectionId: 'tc-9' })], 2, 45));
      await settle(fixture);

      expect(text(fixture)).toContain('Page 2 of 3');
      expect(button(fixture, 'Previous').disabled).toBe(false);
    });

    it('steps back when the last row of the last page was decided', async () => {
      const fixture = await loaded(paged([item()], 1, 21));
      button(fixture, 'Next').click();
      expectList(2).flush(paged([item({ timeCorrectionId: 'tc-21', rowVersion: 'v21' })], 2, 21));
      await settle(fixture);

      button(fixture, 'Approve').click();
      httpMock.expectOne(`${listUrl}/tc-21/approve`).flush(item({ status: 'APPROVED' }));
      expectList(2).flush(paged([], 2, 20)); // page 2 is now empty
      expectList(1).flush(paged([item()], 1, 20));
      await settle(fixture);

      expect(text(fixture)).toContain('Page 1 of 1');
    });
  });
});
