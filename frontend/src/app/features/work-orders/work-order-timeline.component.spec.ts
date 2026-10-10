import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { WorkOrderTimelineComponent } from './work-order-timeline.component';
import { AuditTimelineItem, AuditTimelinePage } from './work-order-timeline.models';

describe('WorkOrderTimelineComponent', () => {
  let httpMock: HttpTestingController;
  const timelineUrl = `${environment.apiBaseUrl}/v1/entities/WORK_ORDER/wo-1/timeline`;

  function item(n: number, overrides: Partial<AuditTimelineItem> = {}): AuditTimelineItem {
    return {
      auditId: `audit-${n}`,
      occurredAt: `2026-10-01T10:${String(59 - n).padStart(2, '0')}:00.123Z`,
      actionCode: 'WORK_ORDER_STARTED',
      entityType: 'WORK_ORDER',
      entityId: 'wo-1',
      fromState: 'SCHEDULED',
      toState: 'IN_PROGRESS',
      actor: { actorId: `0123456789abcdef-${n}`, kind: 'USER' },
      details: {},
      ...overrides,
    };
  }

  function page(items: AuditTimelineItem[], nextCursor: string | null = null): AuditTimelinePage {
    return { items, nextCursor, hasMore: nextCursor !== null };
  }

  function create(refreshKey: string | null = 'v1'): ComponentFixture<WorkOrderTimelineComponent> {
    const fixture = TestBed.createComponent(WorkOrderTimelineComponent);
    fixture.componentRef.setInput('workOrderId', 'wo-1');
    fixture.componentRef.setInput('refreshKey', refreshKey);
    document.body.appendChild(fixture.nativeElement as HTMLElement);
    return fixture;
  }

  const first = () => httpMock.expectOne((request) => request.url === timelineUrl && !request.params.has('cursor'));
  const text = (fixture: ComponentFixture<WorkOrderTimelineComponent>) => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const root = (fixture: ComponentFixture<WorkOrderTimelineComponent>) => fixture.nativeElement as HTMLElement;
  const button = (fixture: ComponentFixture<WorkOrderTimelineComponent>, label: string) =>
    Array.from(root(fixture).querySelectorAll('button')).find((b) => b.textContent?.trim() === label) as HTMLButtonElement | undefined;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [WorkOrderTimelineComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    document.body.querySelectorAll('app-work-order-timeline').forEach((element) => element.remove());
    localStorage.clear();
    sessionStorage.clear();
  });

  // ---------------- states ----------------

  it('shows a loading state, then the events newest first', () => {
    const fixture = create();
    fixture.detectChanges();

    expect(text(fixture)).toContain('Loading timeline');
    expect(root(fixture).querySelector('section')!.getAttribute('aria-busy')).toBe('true');

    const request = first();
    expect(request.request.method).toBe('GET');
    expect(request.request.params.keys()).toEqual([]); // first page: no cursor, default page size
    request.flush(page([item(1), item(2), item(3)]));
    fixture.detectChanges();

    expect(text(fixture)).not.toContain('Loading timeline');
    expect(root(fixture).querySelector('section')!.getAttribute('aria-busy')).toBe('false');
    const rows = root(fixture).querySelectorAll('li.timeline-item');
    expect(rows.length).toBe(3);
    expect(rows[0].textContent).toContain('Work started');
  });

  it('shows an empty state when there is no activity', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(page([]));
    fixture.detectChanges();

    expect(text(fixture)).toContain('No activity recorded yet.');
    expect(root(fixture).querySelector('ol')).toBeNull();
    expect(button(fixture, 'Load more')).toBeUndefined();
  });

  it('shows an alert with Retry on a server error, and Retry reloads the first page', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root(fixture).querySelector('[role="alert"]')!.textContent).toContain('Could not load the timeline');
    const retry = button(fixture, 'Retry')!;
    expect(retry).toBeDefined();

    retry.click();
    fixture.detectChanges();
    first().flush(page([item(1)]));
    fixture.detectChanges();

    expect(root(fixture).querySelector('[role="alert"]')).toBeNull();
    expect(root(fixture).querySelectorAll('li.timeline-item').length).toBe(1);
  });

  it('shows a permission message without Retry on 401/403', () => {
    for (const status of [401, 403]) {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ imports: [WorkOrderTimelineComponent], providers: [provideHttpClient(), provideHttpClientTesting()] });
      httpMock = TestBed.inject(HttpTestingController);
      const fixture = create();
      fixture.detectChanges();
      first().flush('no', { status, statusText: 'Denied' });
      fixture.detectChanges();

      expect(text(fixture)).toContain('your role cannot view this timeline');
      expect(button(fixture, 'Retry')).toBeUndefined();
      httpMock.verify();
    }
  });

  it('shows a not-found message without Retry on 404, without hiding anything else', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush({ code: 'NOT_FOUND' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(text(fixture)).toContain('does not exist, or is outside your data scope');
    expect(button(fixture, 'Retry')).toBeUndefined();
    expect(root(fixture).querySelector('h2')!.textContent).toContain('Timeline');
  });

  // ---------------- Load more ----------------

  it('appends the next page with the opaque cursor, and hides Load more on the last page', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(page([item(1), item(2)], 'cursor-1'));
    fixture.detectChanges();

    expect(button(fixture, 'Load more')).toBeDefined();
    button(fixture, 'Load more')!.click();
    fixture.detectChanges();

    const next = httpMock.expectOne((request) => request.url === timelineUrl && request.params.get('cursor') === 'cursor-1');
    next.flush(page([item(3), item(4)]));
    fixture.detectChanges();

    expect(root(fixture).querySelectorAll('li.timeline-item').length).toBe(4);
    expect(button(fixture, 'Load more')).toBeUndefined();
  });

  it('de-duplicates by auditId when a page repeats an event', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(page([item(1), item(2)], 'cursor-1'));
    fixture.detectChanges();

    button(fixture, 'Load more')!.click();
    httpMock.expectOne((request) => request.params.get('cursor') === 'cursor-1').flush(page([item(2), item(3), item(3)]));
    fixture.detectChanges();

    const ids = Array.from(root(fixture).querySelectorAll('li.timeline-item')).map((li) => li.textContent);
    expect(ids.length).toBe(3);
    expect(new Set(ids).size).toBe(3);
  });

  it('disables Load more while the request is in flight and ignores a second click', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(page([item(1)], 'cursor-1'));
    fixture.detectChanges();

    button(fixture, 'Load more')!.click();
    fixture.detectChanges();
    const loading = button(fixture, 'Loading…')!;
    expect(loading.disabled).toBe(true);

    loading.click();
    root(fixture).querySelector<HTMLButtonElement>('button')!.click();

    // Exactly one request despite the extra clicks (expectOne fails on two).
    httpMock.expectOne((request) => request.params.get('cursor') === 'cursor-1').flush(page([item(2)]));
    fixture.detectChanges();
    expect(root(fixture).querySelectorAll('li.timeline-item').length).toBe(2);
  });

  it('keeps the loaded events and offers Retry when Load more fails, retrying the same cursor', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(page([item(1)], 'cursor-1'));
    fixture.detectChanges();

    button(fixture, 'Load more')!.click();
    httpMock.expectOne((request) => request.params.get('cursor') === 'cursor-1').flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root(fixture).querySelectorAll('li.timeline-item').length).toBe(1);
    expect(root(fixture).querySelector('[role="alert"]')).not.toBeNull();

    button(fixture, 'Retry')!.click();
    httpMock.expectOne((request) => request.params.get('cursor') === 'cursor-1').flush(page([item(2)]));
    fixture.detectChanges();

    expect(root(fixture).querySelectorAll('li.timeline-item').length).toBe(2);
    expect(root(fixture).querySelector('[role="alert"]')).toBeNull();
  });

  it('restarts from the first page when the server no longer accepts the cursor (400)', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(page([item(1), item(2)], 'stale-cursor'));
    fixture.detectChanges();

    button(fixture, 'Load more')!.click();
    httpMock.expectOne((request) => request.params.get('cursor') === 'stale-cursor').flush({ code: 'BAD_REQUEST' }, { status: 400, statusText: 'Bad Request' });
    first().flush(page([item(9)]));
    fixture.detectChanges();

    expect(root(fixture).querySelectorAll('li.timeline-item').length).toBe(1);
  });

  it('moves focus to the first newly loaded event', async () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(page([item(1), item(2)], 'cursor-1'));
    fixture.detectChanges();

    button(fixture, 'Load more')!.click();
    httpMock.expectOne((request) => request.params.get('cursor') === 'cursor-1').flush(page([item(3), item(4)]));
    fixture.detectChanges();
    await fixture.whenStable();

    const rows = root(fixture).querySelectorAll('li.timeline-item');
    expect(document.activeElement).toBe(rows[2]);
  });

  // ---------------- refresh ----------------

  it('reloads from the first page when refreshKey changes, replacing the events', () => {
    const fixture = create('v1');
    fixture.detectChanges();
    first().flush(page([item(1)]));
    fixture.detectChanges();

    fixture.componentRef.setInput('refreshKey', 'v2');
    fixture.detectChanges();
    first().flush(page([item(7), item(8)]));
    fixture.detectChanges();

    expect(root(fixture).querySelectorAll('li.timeline-item').length).toBe(2);
  });

  it('ignores a stale response that arrives after a newer reload', () => {
    const fixture = create('v1');
    fixture.detectChanges();
    const stale = first();

    fixture.componentRef.setInput('refreshKey', 'v2');
    fixture.detectChanges();
    const fresh = httpMock.match((request) => request.url === timelineUrl);

    expect(stale.cancelled).toBe(true); // the superseded request is cancelled
    fresh.find((request) => !request.cancelled)!.flush(page([item(5)]));
    fixture.detectChanges();

    expect(root(fixture).querySelectorAll('li.timeline-item').length).toBe(1);
    expect(text(fixture)).toContain('Work started');
  });

  // ---------------- rendering ----------------

  it('labels known and unknown action codes, and distinguishes the customer rejection', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(
      page([
        item(1, { actionCode: 'WORK_ORDER_REJECTED' }),
        item(2, { actionCode: 'WORK_ORDER_CANCELLED' }),
        item(3, { actionCode: 'SOME_FUTURE_EVENT_CODE' }),
      ]),
    );
    fixture.detectChanges();

    expect(text(fixture)).toContain('Customer rejected work');
    expect(text(fixture)).toContain('Work Order cancelled');
    expect(text(fixture)).toContain('Some future event code');
    expect(text(fixture)).not.toContain('SOME_FUTURE_EVENT_CODE');
  });

  it('renders each time as a semantic <time> with the UTC instant in datetime and title, shown in browser-local time', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(page([item(1, { occurredAt: '2026-10-01T10:30:15.123Z' })]));
    fixture.detectChanges();

    const time = root(fixture).querySelector('time')!;
    expect(time.getAttribute('datetime')).toBe('2026-10-01T10:30:15.123Z');
    expect(time.getAttribute('title')).toBe('UTC 2026-10-01T10:30:15.123Z');
    expect(time.textContent!.trim()).toBe(new Date('2026-10-01T10:30:15.123Z').toLocaleString());
  });

  it('shows the actor as a short id and kind only, never a name or e-mail', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(
      page([
        item(1, { actor: { actorId: 'aaaaaaaa-1111-2222-3333-444444444444', kind: 'USER' } }),
        item(2, { actor: { actorId: '5c4a9b1e-0000-4000-8000-000000000002', kind: 'SYSTEM' } }),
        item(3, { actor: { actorId: 'bbbbbbbb-1111-2222-3333-444444444444', kind: 'UNKNOWN' } }),
      ]),
    );
    fixture.detectChanges();

    const rows = Array.from(root(fixture).querySelectorAll('li.timeline-item')).map((li) => li.textContent ?? '');
    expect(rows[0]).toContain('By user aaaaaaaa');
    expect(rows[0]).not.toContain('44444444');
    expect(rows[1]).toContain('By System');
    expect(rows[2]).toContain('By an unknown user');
  });

  it('shows from/to states when present and omits the line when both are null', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(page([item(1, { fromState: 'OPEN', toState: 'SCHEDULED' }), item(2, { fromState: null, toState: null })]));
    fixture.detectChanges();

    const rows = Array.from(root(fixture).querySelectorAll('li.timeline-item')).map((li) => li.textContent ?? '');
    expect(rows[0]).toContain('OPEN');
    expect(rows[0]).toContain('SCHEDULED');
    expect(rows[1]).not.toContain('→');
  });

  it('renders allowlisted details with humanized keys and never raw JSON', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(
      page([item(1, { actionCode: 'WORK_ORDER_SCHEDULED', details: { serviceVisitId: 'visit-9', assignedTechnicianId: null, ownerTeamId: 7 } })]),
    );
    fixture.detectChanges();

    const content = text(fixture);
    expect(content).toContain('Service visit id');
    expect(content).toContain('visit-9');
    expect(content).toContain('Assigned technician id');
    expect(content).toContain('—');
    expect(content).toContain('Owner team id');
    expect(content).not.toContain('{');
    expect(content).not.toContain('"');
  });

  // ---------------- accessibility and storage ----------------

  it('exposes landmarks, a labelled list and a polite live region', () => {
    const fixture = create();
    fixture.detectChanges();
    first().flush(page([item(1), item(2)], 'c'));
    fixture.detectChanges();

    const section = root(fixture).querySelector('section')!;
    const heading = root(fixture).querySelector('h2')!;
    expect(section.getAttribute('aria-labelledby')).toBe(heading.id);
    expect(root(fixture).querySelector('ol')!.getAttribute('aria-label')).toContain('newest first');
    expect(root(fixture).querySelectorAll('ol > li').length).toBe(2);
    const live = root(fixture).querySelector('[aria-live="polite"]')!;
    expect(live.textContent).toContain('2 timeline events loaded');
  });

  it('never uses localStorage or sessionStorage', () => {
    const setLocal = vi.spyOn(Storage.prototype, 'setItem');
    const getLocal = vi.spyOn(Storage.prototype, 'getItem');
    const fixture = create();
    fixture.detectChanges();
    first().flush(page([item(1)], 'cursor-1'));
    fixture.detectChanges();
    button(fixture, 'Load more')!.click();
    httpMock.expectOne((request) => request.params.get('cursor') === 'cursor-1').flush(page([item(2)]));
    fixture.detectChanges();

    expect(setLocal).not.toHaveBeenCalled();
    expect(getLocal).not.toHaveBeenCalled();
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    setLocal.mockRestore();
    getLocal.mockRestore();
  });
});
