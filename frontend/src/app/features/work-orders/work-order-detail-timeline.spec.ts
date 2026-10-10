import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { environment } from '../../../environments/environment';
import { WorkOrderDetailComponent } from './work-order-detail.component';
import { AuditTimelineItem, AuditTimelinePage } from './work-order-timeline.models';
import { WorkOrder } from './work-order.models';

/**
 * Work Order Detail with the REAL Timeline component (the other detail specs stub it): the Timeline loads once the Work
 * Order has loaded, reloads when a command changes the Work Order's rowVersion, and a Timeline failure never hides the page.
 */
describe('WorkOrderDetailComponent — Timeline integration', () => {
  let httpMock: HttpTestingController;
  const workOrdersUrl = `${environment.apiBaseUrl}/v1/work-orders`;
  const timelineUrl = `${environment.apiBaseUrl}/v1/entities/WORK_ORDER/abc-123/timeline`;

  function tokenWithPayload(payload: Record<string, unknown>): string {
    const base64url = (value: string) => btoa(value).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    return `${base64url('{"alg":"HS256"}')}.${base64url(JSON.stringify(payload))}.signature`;
  }

  const workOrder: WorkOrder = {
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
    cancelReason: null,
  };

  function event(n: number, actionCode = 'WORK_ORDER_SCHEDULED'): AuditTimelineItem {
    return {
      auditId: `audit-${n}`,
      occurredAt: `2026-10-01T10:${String(50 - n).padStart(2, '0')}:00Z`,
      actionCode,
      entityType: 'WORK_ORDER',
      entityId: 'abc-123',
      fromState: null,
      toState: null,
      actor: { actorId: 'aaaaaaaa-0000-0000-0000-000000000001', kind: 'USER' },
      details: {},
    };
  }

  const page = (items: AuditTimelineItem[]): AuditTimelinePage => ({ items, nextCursor: null, hasMore: false });

  function create(): ComponentFixture<WorkOrderDetailComponent> {
    TestBed.configureTestingModule({
      imports: [WorkOrderDetailComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'abc-123' }) } } },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(WorkOrderDetailComponent);
  }

  const text = (fixture: ComponentFixture<WorkOrderDetailComponent>) => (fixture.nativeElement as HTMLElement).textContent ?? '';

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('loads the Timeline after the Work Order and renders it below the detail', () => {
    const fixture = create();
    fixture.detectChanges();
    httpMock.expectNone((request) => request.url === timelineUrl); // not before the Work Order has loaded

    httpMock.expectOne(`${workOrdersUrl}/abc-123`).flush(workOrder);
    fixture.detectChanges();
    httpMock.expectOne((request) => request.url === timelineUrl && !request.params.has('cursor')).flush(page([event(1), event(2, 'WORK_ORDER_CANCELLED')]));
    fixture.detectChanges();

    const content = text(fixture);
    expect(content).toContain('Work Order WO-1');
    expect(content).toContain('Timeline');
    expect(content).toContain('Work Order scheduled');
    expect(content).toContain('Work Order cancelled');
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('app-work-order-timeline')).not.toBeNull();
  });

  it('does not request a Timeline when the Work Order itself is not found', () => {
    const fixture = create();
    fixture.detectChanges();

    httpMock.expectOne(`${workOrdersUrl}/abc-123`).flush('Not Found', { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    httpMock.expectNone((request) => request.url === timelineUrl);
    expect(text(fixture)).toContain('does not exist, or is outside your data scope');
  });

  it('reloads the Timeline after a command changes the Work Order rowVersion', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'sup-1', role: 'SUPERVISOR' }));
    const fixture = create();
    fixture.detectChanges();
    httpMock.expectOne(`${workOrdersUrl}/abc-123`).flush(workOrder);
    fixture.detectChanges();
    httpMock.expectOne((request) => request.url === timelineUrl).flush(page([event(1)]));
    fixture.detectChanges();

    // Cancel the Work Order: the response carries a new rowVersion, which re-keys the Timeline.
    const root = fixture.nativeElement as HTMLElement;
    const form = Array.from(root.querySelectorAll('form')).find((f) => f.textContent?.includes('Cancel Work Order'))!;
    form.querySelector('input')!.value = 'No longer needed.';
    form.dispatchEvent(new Event('submit'));
    httpMock
      .expectOne(`${workOrdersUrl}/abc-123/cancel`)
      .flush({ ...workOrder, status: 'CANCELLED', rowVersion: 'v2', cancelReason: 'No longer needed.' });
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === timelineUrl && !request.params.has('cursor')).flush(page([event(3, 'WORK_ORDER_CANCELLED'), event(1)]));
    fixture.detectChanges();

    expect(text(fixture)).toContain('Work Order cancelled');
    expect(root.querySelectorAll('li.timeline-item').length).toBe(2);
  });

  it('keeps the whole detail page when the Timeline fails, and shows its own alert', () => {
    const fixture = create();
    fixture.detectChanges();
    httpMock.expectOne(`${workOrdersUrl}/abc-123`).flush(workOrder);
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === timelineUrl).flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    const content = text(fixture);
    expect(content).toContain('Work Order WO-1');
    expect(content).toContain('Repair Request No.');
    expect(content).toContain('Could not load the timeline');
    expect((fixture.nativeElement as HTMLElement).querySelector('h1')).not.toBeNull();
  });
});
