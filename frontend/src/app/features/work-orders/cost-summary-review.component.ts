import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { CostSummary, PendingCostSummaryReview, WorkOrderService } from './work-order.service';

const PAGE_SIZE = 20;

/**
 * Supervisor review of Cost Summaries awaiting Review (`docs/13` §4.19 — a technical addition; no baseline Use
 * Case, queue, or read endpoint exists, all approved as such). Lists every COMPLETED Work Order with an
 * unreviewed Cost Summary, within the caller's Site scope. "View Details" fetches and shows the prepared
 * amount/currency/note/preparer inline via the Cost Summary read endpoint — there are no itemized cost
 * components in the baseline, only the single aggregate `totalAmount`. "Review" marks it reviewed with no body
 * (`reviewedBy`/`reviewedAt` are always server-derived) and removes it from this list. Separation of Duties (the
 * reviewer must not be the Cost Summary's own preparer, even holding both roles) is enforced server-side; this
 * page shows no special client-side hint for it, since the backend's 403 message already covers it.
 */
@Component({
  selector: 'app-cost-summary-review',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2>Cost Summaries Awaiting Review</h2>

    @if (items().length === 0 && !loadError()) {
      <p>No Cost Summaries are awaiting review.</p>
    }

    @if (loadError()) {
      <p role="alert">{{ loadError() }}</p>
    }

    @if (itemError()) {
      <p role="alert">{{ itemError() }}</p>
    }

    <ul>
      @for (item of items(); track item.workOrderId) {
        <li>
          <span>{{ item.workOrderNo }}</span>
          <span>{{ item.siteCode ?? '—' }}</span>
          <span>{{ item.totalAmount }} {{ item.currencyCode }}</span>

          <button type="button" [disabled]="submitting()" (click)="toggleDetail(item.workOrderId)">
            @if (expandedId() === item.workOrderId) {
              Hide Details
            } @else {
              View Details
            }
          </button>

          @if (expandedId() === item.workOrderId) {
            @if (detail(); as loaded) {
              <dl>
                <dt>Total Amount</dt>
                <dd>{{ loaded.totalAmount }} {{ loaded.currencyCode }}</dd>
                <dt>Note</dt>
                <dd>{{ loaded.note ?? '—' }}</dd>
                <dt>Prepared By</dt>
                <dd>{{ loaded.preparedBy }}</dd>
                <dt>Prepared At</dt>
                <dd>{{ loaded.preparedAt }}</dd>
              </dl>
            }
            @if (detailError()) {
              <p role="alert">{{ detailError() }}</p>
            }
          }

          <button type="button" [disabled]="submitting()" (click)="onReview(item)">Review</button>
        </li>
      }
    </ul>
  `,
})
export class CostSummaryReviewComponent {
  private readonly workOrders = inject(WorkOrderService);

  protected readonly items = signal<PendingCostSummaryReview[]>([]);
  protected readonly loadError = signal<string | null>(null);

  protected readonly expandedId = signal<string | null>(null);
  protected readonly detail = signal<CostSummary | null>(null);
  protected readonly detailError = signal<string | null>(null);

  protected readonly submitting = signal(false);
  protected readonly itemError = signal<string | null>(null);

  constructor() {
    this.refresh();
  }

  refresh(): void {
    this.loadError.set(null);

    this.workOrders.listPendingCostSummaryReviews(1, PAGE_SIZE).subscribe({
      next: (page) => this.items.set(page.items),
      error: (response: HttpErrorResponse) => {
        this.items.set([]);
        if (response.status !== 401 && response.status !== 403) {
          this.loadError.set(this.fallbackMessage(response));
        }
      },
    });
  }

  protected toggleDetail(workOrderId: string): void {
    if (this.expandedId() === workOrderId) {
      this.expandedId.set(null);
      this.detail.set(null);
      this.detailError.set(null);
      return;
    }

    this.expandedId.set(workOrderId);
    this.detail.set(null);
    this.detailError.set(null);

    this.workOrders.getCostSummary(workOrderId).subscribe({
      next: (loaded) => this.detail.set(loaded),
      error: (response: HttpErrorResponse) => this.detailError.set(this.fallbackMessage(response)),
    });
  }

  /** Fetches the current Cost Summary first, so If-Match always targets its own fresh rowVersion, never a stale value held from the list/detail render. */
  protected onReview(item: PendingCostSummaryReview): void {
    this.submitting.set(true);
    this.itemError.set(null);

    this.workOrders.getCostSummary(item.workOrderId).subscribe({
      next: (costSummary) => {
        this.workOrders.reviewCostSummary(item.workOrderId, `"${costSummary.rowVersion}"`).subscribe({
          next: () => {
            this.submitting.set(false);
            if (this.expandedId() === item.workOrderId) {
              this.expandedId.set(null);
              this.detail.set(null);
            }

            this.items.set(this.items().filter((existing) => existing.workOrderId !== item.workOrderId));
          },
          error: (response: HttpErrorResponse) => {
            this.submitting.set(false);

            if (response.status === 404 || response.status === 409) {
              // The Cost Summary changed, was already reviewed, or is gone: show the real list instead of a stale row.
              this.itemError.set(this.describe(response));
              this.refresh();
              return;
            }

            this.itemError.set(this.describe(response));
          },
        });
      },
      error: (response: HttpErrorResponse) => {
        this.submitting.set(false);
        this.itemError.set(this.describe(response));

        if (response.status === 404) {
          // The Cost Summary is gone or out of scope before Review was even attempted: the queue item is stale.
          this.refresh();
        }
      },
    });
  }

  private describe(response: HttpErrorResponse): string {
    const problem = response.error as { code?: string; detail?: string } | null;

    switch (response.status) {
      case 401:
        return 'You are not signed in, or your session has expired. Please sign in again.';
      case 403:
        return 'You do not have permission to review this Cost Summary.';
      case 404:
        return 'This Cost Summary was not found, or it is no longer awaiting review.';
      case 409:
        return problem?.code === 'STATE_CONFLICT' && problem.detail
          ? problem.detail
          : 'This Cost Summary was changed by another request. The latest list is shown above.';
      default:
        return this.fallbackMessage(response);
    }
  }

  private fallbackMessage(response: HttpErrorResponse): string {
    return response.status === 0
      ? 'Could not reach the server. Check your connection and try again.'
      : 'Something went wrong. Please try again.';
  }
}
