import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { MyVisitSummary } from './my-visits.models';
import { MyVisitsComponent } from './my-visits.component';

describe('MyVisitsComponent', () => {
  let httpMock: HttpTestingController;
  const baseUrl = `${environment.apiBaseUrl}/v1/service-visits`;

  const visit: MyVisitSummary = {
    serviceVisitId: 'visit-1',
    workOrderId: 'wo-1',
    workOrderNo: 'WO-1',
    status: 'SCHEDULED',
    siteCode: 'SITE-A',
    equipmentCode: 'EQ-1',
    scheduledStartAt: '2026-10-01T08:00:00Z',
    scheduledEndAt: '2026-10-01T10:00:00Z',
    rowVersion: 'v1',
  };

  function createComponent(): ComponentFixture<MyVisitsComponent> {
    TestBed.configureTestingModule({
      imports: [MyVisitsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(MyVisitsComponent);
  }

  const sessionUrl = `${environment.apiBaseUrl}/v1/work-sessions/current`;

  afterEach(() => {
    // The embedded Active Work Session panel loads the current session on init and after a Check-in. These specs
    // are about the visits list, so answer any such request with "no session" before verifying nothing is left.
    httpMock.match((request) => request.url === sessionUrl).forEach((request) => request.flush(null));
    httpMock.verify();
  });

  it('asks the Active Work Session panel to reload after a successful Check-in', () => {
    const fixture = createComponent();
    fixture.detectChanges();
    httpMock.expectOne((request) => request.url === `${baseUrl}/mine`).flush({ items: [visit], page: 1, pageSize: 20, totalCount: 1 });
    fixture.detectChanges();
    // The panel's own initial load (before any Check-in).
    expect(httpMock.match((request) => request.url === sessionUrl).length).toBe(1);

    (fixture.nativeElement as HTMLElement).querySelector('button')!.dispatchEvent(new Event('click'));
    httpMock.expectOne(`${baseUrl}/visit-1/check-in`).flush({});

    // After the Check-in the panel reloads the current session (a new session is not in the visits list).
    const reload = httpMock.match((request) => request.url === sessionUrl);
    expect(reload.length).toBe(1);
    reload[0].flush(null);
    httpMock.expectOne((request) => request.url === `${baseUrl}/mine`).flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  });

  it('requests the list on init and renders each visit', () => {
    const fixture = createComponent();
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === `${baseUrl}/mine`).flush({ items: [visit], page: 1, pageSize: 20, totalCount: 1 });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('WO-1');
    expect(text).toContain('SITE-A');
  });

  it('shows an unauthorized message on 401/403', () => {
    const fixture = createComponent();
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === `${baseUrl}/mine`).flush('Forbidden', { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('not signed in');
  });

  it('shows an empty message when there are no assigned visits', () => {
    const fixture = createComponent();
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === `${baseUrl}/mine`).flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No assigned visits');
  });

  it('checks in with the quoted rowVersion, then reloads the list', () => {
    const fixture = createComponent();
    fixture.detectChanges();
    httpMock.expectOne((request) => request.url === `${baseUrl}/mine`).flush({ items: [visit], page: 1, pageSize: 20, totalCount: 1 });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector('button')!.dispatchEvent(new Event('click'));

    const req = httpMock.expectOne(`${baseUrl}/visit-1/check-in`);
    expect(req.request.headers.get('If-Match')).toBe('"v1"');
    expect(req.request.body).toBeNull();
    req.flush({});

    httpMock.expectOne((request) => request.url === `${baseUrl}/mine`).flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    fixture.detectChanges();

    expect(root.textContent).toContain('No assigned visits');
  });

  function loadOneVisitAndClickCheckIn(): { fixture: ComponentFixture<MyVisitsComponent>; root: HTMLElement } {
    const fixture = createComponent();
    fixture.detectChanges();
    httpMock.expectOne((request) => request.url === `${baseUrl}/mine`).flush({ items: [visit], page: 1, pageSize: 20, totalCount: 1 });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector('button')!.dispatchEvent(new Event('click'));
    return { fixture, root };
  }

  it('shows the server message when Check-in returns a STATE_CONFLICT (e.g. an active session elsewhere)', () => {
    const { fixture, root } = loadOneVisitAndClickCheckIn();

    httpMock.expectOne(`${baseUrl}/visit-1/check-in`).flush(
      { code: 'STATE_CONFLICT', detail: 'You already have an active Work Session on another Visit. Check out before checking in elsewhere.' },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    expect(root.textContent).toContain('You already have an active Work Session on another Visit');
  });

  it('shows a plain not-found message when Check-in returns 404', () => {
    const { fixture, root } = loadOneVisitAndClickCheckIn();

    httpMock.expectOne(`${baseUrl}/visit-1/check-in`).flush('Not Found', { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(root.textContent).toContain('no longer assigned to you');
    expect(root.textContent).not.toContain('Http failure');
  });

  it('shows a plain permission message when Check-in returns 403', () => {
    const { fixture, root } = loadOneVisitAndClickCheckIn();

    httpMock.expectOne(`${baseUrl}/visit-1/check-in`).flush('Forbidden', { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(root.textContent).toContain('do not have permission');
  });

  it('shows a generic message, not the raw HTTP error, when Check-in fails with a server error', () => {
    const { fixture, root } = loadOneVisitAndClickCheckIn();

    httpMock.expectOne(`${baseUrl}/visit-1/check-in`).flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(root.textContent).toContain('Something went wrong');
    expect(root.textContent).not.toContain('Http failure');
  });

  it('shows a plain message, not the raw HTTP error, when the list fails to load', () => {
    const fixture = createComponent();
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === `${baseUrl}/mine`).flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Something went wrong');
    expect(text).not.toContain('Http failure');
  });

  it('shows a distinct message when Check-in returns 409', () => {
    const fixture = createComponent();
    fixture.detectChanges();
    httpMock.expectOne((request) => request.url === `${baseUrl}/mine`).flush({ items: [visit], page: 1, pageSize: 20, totalCount: 1 });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    root.querySelector('button')!.dispatchEvent(new Event('click'));

    httpMock.expectOne(`${baseUrl}/visit-1/check-in`).flush('Conflict', { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(root.textContent).toContain('changed or is no longer in a valid state');
  });
});
