import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { environment } from '../../../environments/environment';
import { CostSummaryReviewComponent } from './cost-summary-review.component';
import { PendingCostSummaryReview } from './work-order.service';

describe('CostSummaryReviewComponent', () => {
  let httpMock: HttpTestingController;
  const workOrdersBaseUrl = `${environment.apiBaseUrl}/v1/work-orders`;
  const queueUrl = `${workOrdersBaseUrl}/pending-cost-summary-review`;

  const item: PendingCostSummaryReview = {
    workOrderId: 'wo-1',
    workOrderNo: 'WO-1',
    siteCode: 'SITE-A',
    totalAmount: 1234.56,
    currencyCode: 'USD',
    preparedBy: 'tl-1',
    preparedAt: '2026-09-27T00:00:00Z',
  };

  const costSummaryDetail = {
    costSummaryId: 'cs-1',
    workOrderId: 'wo-1',
    totalAmount: 1234.56,
    currencyCode: 'USD',
    note: 'Parts and labor.',
    preparedBy: 'tl-1',
    preparedAt: '2026-09-27T00:00:00Z',
    reviewedBy: null,
    reviewedAt: null,
    rowVersion: 'cs-v1',
  };

  function createComponent(): ComponentFixture<CostSummaryReviewComponent> {
    TestBed.configureTestingModule({
      imports: [CostSummaryReviewComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    return TestBed.createComponent(CostSummaryReviewComponent);
  }

  afterEach(() => httpMock.verify());

  function loadOneItem(): { fixture: ComponentFixture<CostSummaryReviewComponent>; root: HTMLElement } {
    const fixture = createComponent();
    fixture.detectChanges();
    httpMock
      .expectOne((request) => request.url === queueUrl)
      .flush({ items: [item], page: 1, pageSize: 20, totalCount: 1 });
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  function buttonNamed(root: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(root.querySelectorAll('button')).find((button) => button.textContent?.trim() === text);
  }

  it('requests the pending-review queue on init and renders each item', () => {
    const { root } = loadOneItem();

    expect(root.textContent).toContain('WO-1');
    expect(root.textContent).toContain('SITE-A');
    expect(root.textContent).toContain('1234.56');
  });

  it('shows an empty message when there is nothing to review', () => {
    const fixture = createComponent();
    fixture.detectChanges();
    httpMock.expectOne((request) => request.url === queueUrl).flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No Cost Summaries are awaiting review');
  });

  it('fetches and shows the Cost Summary detail when View Details is clicked, then hides it on toggle', () => {
    const { fixture, root } = loadOneItem();

    buttonNamed(root, 'View Details')!.click();
    fixture.detectChanges();

    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/cost-summary`).flush(costSummaryDetail);
    fixture.detectChanges();

    expect(root.textContent).toContain('Parts and labor.');
    expect(root.textContent).toContain('tl-1');

    buttonNamed(root, 'Hide Details')!.click();
    fixture.detectChanges();

    expect(root.textContent).not.toContain('Parts and labor.');
  });

  it('reviews by first re-fetching the current Cost Summary rowVersion, then submitting review with it, and removes the item from the list', () => {
    const { fixture, root } = loadOneItem();

    buttonNamed(root, 'Review')!.click();

    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/cost-summary`).flush(costSummaryDetail);

    const reviewReq = httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/review-cost-summary`);
    expect(reviewReq.request.headers.get('If-Match')).toBe('"cs-v1"');
    expect(reviewReq.request.body).toBeNull();
    reviewReq.flush({ ...costSummaryDetail, reviewedBy: 'sup-1', reviewedAt: '2026-09-27T01:00:00Z', rowVersion: 'cs-v2' });
    fixture.detectChanges();

    expect(root.textContent).toContain('No Cost Summaries are awaiting review');
  });

  it('disables Review and View Details while a Review request is in flight, preventing a double submit', () => {
    const { fixture, root } = loadOneItem();

    buttonNamed(root, 'Review')!.click();
    fixture.detectChanges();

    expect(buttonNamed(root, 'Review')!.disabled).toBe(true);
    expect(buttonNamed(root, 'View Details')!.disabled).toBe(true);

    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/cost-summary`).flush(costSummaryDetail);
    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/review-cost-summary`).flush(costSummaryDetail);
  });

  it('shows a 403 message when the caller is the Cost Summary\'s own preparer (Separation of Duties)', () => {
    const { fixture, root } = loadOneItem();

    buttonNamed(root, 'Review')!.click();
    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/cost-summary`).flush(costSummaryDetail);
    httpMock
      .expectOne(`${workOrdersBaseUrl}/wo-1/review-cost-summary`)
      .flush({ code: 'ACCESS_DENIED' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(root.textContent).toContain('You do not have permission to review this Cost Summary.');
  });

  it('shows the server message and reloads when Review returns a STATE_CONFLICT (already reviewed)', () => {
    const { fixture, root } = loadOneItem();

    buttonNamed(root, 'Review')!.click();
    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/cost-summary`).flush(costSummaryDetail);
    httpMock
      .expectOne(`${workOrdersBaseUrl}/wo-1/review-cost-summary`)
      .flush({ code: 'STATE_CONFLICT', detail: 'This Work Order\'s Cost Summary has already been reviewed.' }, { status: 409, statusText: 'Conflict' });
    httpMock.expectOne((request) => request.url === queueUrl).flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    fixture.detectChanges();

    expect(root.textContent).toContain('This Work Order\'s Cost Summary has already been reviewed.');
    expect(root.textContent).toContain('No Cost Summaries are awaiting review');
  });

  it('shows a not-found message and reloads when the Cost Summary read returns 404', () => {
    const { fixture, root } = loadOneItem();

    buttonNamed(root, 'Review')!.click();
    httpMock.expectOne(`${workOrdersBaseUrl}/wo-1/cost-summary`).flush('Not Found', { status: 404, statusText: 'Not Found' });
    httpMock.expectOne((request) => request.url === queueUrl).flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
    fixture.detectChanges();

    expect(root.textContent).toContain('This Cost Summary was not found, or it is no longer awaiting review.');
    expect(root.textContent).toContain('No Cost Summaries are awaiting review');
  });
});
