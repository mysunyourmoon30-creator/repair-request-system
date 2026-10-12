import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { ServiceReport, ServiceReportWithCost } from './service-report.models';
import { ServiceReportComponent } from './service-report.component';

function tokenWithPayload(payload: Record<string, unknown>): string {
  const base64url = (value: string) => btoa(value).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${base64url('{"alg":"HS256"}')}.${base64url(JSON.stringify(payload))}.signature`;
}

describe('ServiceReportComponent', () => {
  let httpMock: HttpTestingController;
  const reportUrl = `${environment.apiBaseUrl}/v1/work-orders/wo-1/service-report`;
  const contentUrl = (evidenceId: string) => `${environment.apiBaseUrl}/v1/work-orders/wo-1/evidence/${evidenceId}/content`;

  const operational: ServiceReport = {
    workOrder: {
      workOrderId: 'wo-1',
      workOrderNo: 'WO-0001',
      status: 'AWAITING_SUPERVISOR_REVIEW',
      createdAt: '2026-10-01T07:00:00Z',
      closedAt: null,
      repairRequestId: 'rr-1',
      repairRequestNo: 'RR-2026-000001',
      customerCode: 'CUST-1',
      siteCode: 'SITE-A',
      equipmentCode: 'EQ-1',
    },
    visits: [
      {
        serviceVisitId: 'visit-1',
        visitType: 'INITIAL',
        status: 'COMPLETED',
        assignedTechnicianId: 'tech-1',
        scheduledStartAt: '2026-10-01T08:00:00Z',
        scheduledEndAt: '2026-10-01T10:00:00Z',
        completedAt: '2026-10-01T09:30:00Z',
        sessions: [
          {
            workSessionId: 's1',
            status: 'CHECKED_OUT',
            checkInAt: '2026-10-01T08:05:00Z',
            checkOutAt: '2026-10-01T09:25:00Z',
            pauses: [{ pausedAt: '2026-10-01T08:30:00Z', resumedAt: '2026-10-01T08:40:00Z' }],
          },
        ],
      },
    ],
    workSummaries: [
      {
        workSummaryId: 'ws-1',
        serviceVisitId: 'visit-1',
        revisionNo: 1,
        summaryText: 'Replaced the worn bearing.',
        repairOutcomeCode: 'REPAIRED',
        evidence: [
          { evidenceId: 'ev-1', evidenceType: 'BEFORE_WORK', fileName: 'before.png', mimeType: 'image/png', sizeBytes: 2048, slotNo: 1, boundAt: '2026-10-01T09:31:00Z' },
          { evidenceId: 'ev-2', evidenceType: 'AFTER_WORK', fileName: 'after.pdf', mimeType: 'application/pdf', sizeBytes: 3 * 1024 * 1024, slotNo: 2, boundAt: '2026-10-01T09:31:00Z' },
        ],
      },
    ],
    acceptance: [
      { roundNo: 1, decision: 'REJECT', decisionReason: 'Still noisy.', decidedAt: '2026-10-01T11:00:00Z', acceptanceContactId: 'c1' },
      { roundNo: 2, decision: 'ACCEPT', decisionReason: null, decidedAt: '2026-10-01T12:00:00Z', acceptanceContactId: 'c1' },
    ],
  };

  const withCost: ServiceReportWithCost = {
    ...operational,
    costSummary: {
      totalAmount: 4821.37,
      currencyCode: 'THB',
      note: 'Parts and labour',
      preparedBy: 'tl-1',
      preparedAt: '2026-10-01T13:00:00Z',
      reviewedBy: 'sup-1',
      reviewedAt: '2026-10-01T14:00:00Z',
    },
  };

  function create(): { fixture: ComponentFixture<ServiceReportComponent>; root: HTMLElement } {
    TestBed.configureTestingModule({
      imports: [ServiceReportComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'wo-1' }) } } },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(ServiceReportComponent);
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  function load(body: object): { fixture: ComponentFixture<ServiceReportComponent>; root: HTMLElement } {
    const created = create();
    httpMock.expectOne(reportUrl).flush(body);
    created.fixture.detectChanges();
    return created;
  }

  function fail(status: number): { fixture: ComponentFixture<ServiceReportComponent>; root: HTMLElement } {
    const created = create();
    httpMock.expectOne(reportUrl).flush({ code: 'X' }, { status, statusText: 'Error' });
    created.fixture.detectChanges();
    return created;
  }

  function buttonNamed(root: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  }

  beforeEach(() => localStorage.clear());
  afterEach(() => httpMock.verify());

  // ---------------- states ----------------

  it('shows a loading status while the report is being fetched', () => {
    const { root } = create();

    expect(root.querySelector('[role="status"]')?.textContent).toContain('Loading the Service Report');
    httpMock.expectOne(reportUrl).flush(operational);
  });

  it('requests exactly one report for the Work Order in the route', () => {
    create();

    const request = httpMock.expectOne(reportUrl);
    expect(request.request.method).toBe('GET');
    request.flush(operational);
  });

  it.each([
    [401, 'not signed in'],
    [403, 'role cannot view'],
    [404, 'does not exist, or you do not have access'],
  ])('shows a plain message for %i without any report content', (status, text) => {
    const { root } = fail(status);

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(text);
    expect(root.querySelector('h1')).toBeNull();
    expect(buttonNamed(root, 'Retry')).toBeUndefined();
  });

  it('shows a generic failure with a Retry that reloads', () => {
    const { fixture, root } = fail(500);

    expect(root.querySelector('[role="alert"]')?.textContent).toContain('could not be loaded');
    buttonNamed(root, 'Retry')!.click();
    fixture.detectChanges();
    httpMock.expectOne(reportUrl).flush(operational);
    fixture.detectChanges();

    expect(root.querySelector('h1')?.textContent).toContain('WO-0001');
    expect(root.querySelector('[role="alert"]')).toBeNull();
  });

  // ---------------- operational content ----------------

  it('renders the header, visits with sessions and pauses, the summary, the evidence and the acceptance rounds', () => {
    const { root } = load(operational);
    const text = root.textContent ?? '';

    expect(root.querySelector('h1')?.textContent).toContain('Service Report WO-0001');
    for (const expected of ['RR-2026-000001', 'CUST-1', 'SITE-A', 'EQ-1', 'INITIAL', 'CHECKED_OUT', 'Paused 2026-10-01T08:30:00Z']) {
      expect(text).toContain(expected);
    }

    expect(text).toContain('Replaced the worn bearing.');
    expect(text).toContain('REPAIRED');
    expect(text).toContain('Before work — before.png (2 KB)');
    expect(text).toContain('After work — after.pdf (3.0 MB)');
    expect(text).toContain('Round 1');
    expect(text).toContain('REJECT');
    expect(text).toContain('Still noisy.');
    expect(text).toContain('ACCEPT');
  });

  it('uses labelled sections and a named evidence list for accessibility', () => {
    const { root } = load(operational);

    const sections = Array.from(root.querySelectorAll('section[aria-labelledby]'));
    expect(sections.length).toBeGreaterThanOrEqual(4);
    for (const section of sections) {
      expect(root.querySelector(`#${section.getAttribute('aria-labelledby')}`)?.textContent?.trim()).toBeTruthy();
    }

    expect(root.querySelector('ul[aria-label^="Evidence of"]')).not.toBeNull();
    expect(root.querySelector('button[aria-label="Download before.png"]')).not.toBeNull();
  });

  it('shows plain empty states when nothing has happened yet', () => {
    const { root } = load({ ...operational, visits: [], workSummaries: [], acceptance: [] });
    const text = root.textContent ?? '';

    expect(text).toContain('No Service Visit has been recorded.');
    expect(text).toContain('No Work Summary has been submitted yet.');
    expect(text).toContain('No customer decision has been recorded yet.');
  });

  it('shows an empty evidence state for a summary without bound evidence', () => {
    const { root } = load({ ...operational, workSummaries: [{ ...operational.workSummaries[0], evidence: [] }] });

    expect(root.textContent).toContain('No evidence is bound to this Work Summary.');
    expect(root.querySelector('button[aria-label^="Download"]')).toBeNull();
  });

  // ---------------- cost visibility is response-driven ----------------

  it('renders no Cost Summary section at all when the response has no costSummary property', () => {
    const { root } = load(operational);

    expect(Array.from(root.querySelectorAll('h2')).map((heading) => heading.textContent?.trim())).not.toContain('Cost Summary');
    for (const forbidden of ['4821', 'THB', 'Parts and labour', 'Total', 'Prepared', 'Reviewed']) {
      expect(root.textContent).not.toContain(forbidden);
    }
  });

  it('renders the Cost Summary section when — and only when — the response carries it', () => {
    const { root } = load(withCost);

    expect(Array.from(root.querySelectorAll('h2')).map((heading) => heading.textContent?.trim())).toContain('Cost Summary');
    expect(root.textContent).toContain('4821.37 THB');
    expect(root.textContent).toContain('Parts and labour');
  });

  it('says no Cost Summary was prepared when the full response carries a null section', () => {
    const { root } = load({ ...operational, costSummary: null });

    expect(root.textContent).toContain('No Cost Summary has been prepared yet.');
  });

  it('never decides cost visibility from the role claim in the token', () => {
    // A Team Lead token with an operational response: no cost section. A token without any role with a full response: cost shown.
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'u1', role: ['TEAM_LEAD', 'SUPERVISOR'] }));
    const first = load(operational);
    expect(first.root.textContent).not.toContain('Cost Summary');
    TestBed.resetTestingModule();

    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'u2' }));
    const second = load(withCost);
    expect(second.root.textContent).toContain('Cost Summary');
  });

  // ---------------- evidence download ----------------

  describe('evidence download', () => {
    let created: string[];
    let revoked: string[];

    beforeEach(() => {
      created = [];
      revoked = [];
      URL.createObjectURL = (blob: Blob | MediaSource) => {
        created.push(`blob:${(blob as Blob).size}`);
        return `blob:test-${created.length}`;
      };
      URL.revokeObjectURL = (url: string) => {
        revoked.push(url);
      };
    });

    it('downloads through the authorized endpoint as a Blob and releases the object URL', () => {
      const { fixture, root } = load(operational);

      buttonNamed(root, 'Download')!.click();
      fixture.detectChanges();
      const request = httpMock.expectOne(contentUrl('ev-1'));
      expect(request.request.method).toBe('GET');
      expect(request.request.responseType).toBe('blob');
      request.flush(new Blob(['png-bytes'], { type: 'image/png' }));
      fixture.detectChanges();

      expect(created).toHaveLength(1);
      expect(revoked).toEqual(['blob:test-1']);
      expect(root.querySelector('[role="alert"]')).toBeNull();
    });

    it('disables every download button and ignores a second click while one request is in flight', () => {
      const { fixture, root } = load(operational);
      const buttons = Array.from(root.querySelectorAll<HTMLButtonElement>('button[aria-label^="Download"]'));

      buttons[0].click();
      fixture.detectChanges();
      expect(buttons[0].textContent?.trim()).toBe('Downloading…');
      expect(buttons.every((button) => button.disabled)).toBe(true);

      buttons[0].click();
      buttons[1].click();
      fixture.detectChanges();

      const request = httpMock.expectOne(contentUrl('ev-1'));
      httpMock.expectNone(contentUrl('ev-2'));
      request.flush(new Blob(['x']));
      fixture.detectChanges();
      expect(buttons.every((button) => !button.disabled)).toBe(true);
    });

    it.each([403, 404, 409])('shows a plain not-available message when the server refuses (%i), without leaking details', (status) => {
      const { fixture, root } = load(operational);

      buttonNamed(root, 'Download')!.click();
      fixture.detectChanges();
      httpMock.expectOne(contentUrl('ev-1')).flush(new Blob(['storage/path/secret']), { status, statusText: 'Refused' });
      fixture.detectChanges();

      expect(root.querySelector('[role="alert"]')?.textContent).toContain('not available for download');
      expect(root.textContent).not.toContain('storage/path/secret');
      expect(created).toHaveLength(0);
    });

    it('shows a retryable message for a server error and allows another attempt', () => {
      const { fixture, root } = load(operational);

      buttonNamed(root, 'Download')!.click();
      fixture.detectChanges();
      httpMock.expectOne(contentUrl('ev-1')).flush(new Blob(['x']), { status: 500, statusText: 'Server Error' });
      fixture.detectChanges();
      expect(root.querySelector('[role="alert"]')?.textContent).toContain('Try again');

      buttonNamed(root, 'Download')!.click();
      fixture.detectChanges();
      httpMock.expectOne(contentUrl('ev-1')).flush(new Blob(['x']));
      fixture.detectChanges();
      expect(root.querySelector('[role="alert"]')).toBeNull();
    });
  });
});
