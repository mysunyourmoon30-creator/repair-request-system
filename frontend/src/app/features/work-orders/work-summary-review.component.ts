import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { WorkOrder } from './work-order.models';
import { WorkOrderService } from './work-order.service';
import { WorkSummary } from './work-summary.models';
import { WorkSummaryService } from './work-summary.service';

const PAGE_SIZE = 20;

/**
 * Team Lead / Supervisor review of Work Orders awaiting Work Summary review (`docs/13` §4.15). Lists every Work
 * Order in `AWAITING_SUPERVISOR_REVIEW` status within the caller's site scope (reuses the existing S2-001 list
 * endpoint, unchanged — Team Lead/Supervisor already have `WorkOrder.Read` scope for it); "View Summary" fetches
 * and shows the submitted `summaryText`/`repairOutcomeCode` inline; "Submit for Acceptance" moves the Work Order
 * to `AWAITING_CUSTOMER_ACCEPTANCE` and removes it from this list. Either role may submit for acceptance — there
 * is no separate "Team Lead approves, then Supervisor decides" step in this ticket's scope.
 */
@Component({
  selector: 'app-work-summary-review',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2>Work Summaries Awaiting Review</h2>

    @if (items().length === 0 && !loadError()) {
      <p>No Work Orders are awaiting review.</p>
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
          <span>{{ item.equipmentCode ?? '—' }}</span>

          <button type="button" [disabled]="submittingId() === item.workOrderId" (click)="toggleSummary(item.workOrderId)">
            @if (expandedId() === item.workOrderId) {
              Hide Summary
            } @else {
              View Summary
            }
          </button>

          @if (expandedId() === item.workOrderId) {
            @if (summary(); as loaded) {
              <dl>
                <dt>Repair Outcome</dt>
                <dd>{{ loaded.repairOutcomeCode }}</dd>
                <dt>Summary</dt>
                <dd>{{ loaded.summaryText }}</dd>
              </dl>
            }
            @if (summaryError()) {
              <p role="alert">{{ summaryError() }}</p>
            }
          }

          <button type="button" [disabled]="submittingId() === item.workOrderId" (click)="onSubmitForAcceptance(item)">
            Submit for Acceptance
          </button>
        </li>
      }
    </ul>
  `,
})
export class WorkSummaryReviewComponent {
  private readonly workOrders = inject(WorkOrderService);
  private readonly workSummaries = inject(WorkSummaryService);

  protected readonly items = signal<WorkOrder[]>([]);
  protected readonly loadError = signal<string | null>(null);

  protected readonly expandedId = signal<string | null>(null);
  protected readonly summary = signal<WorkSummary | null>(null);
  protected readonly summaryError = signal<string | null>(null);

  protected readonly submittingId = signal<string | null>(null);
  protected readonly itemError = signal<string | null>(null);

  constructor() {
    this.refresh();
  }

  refresh(): void {
    this.loadError.set(null);

    this.workOrders.list(1, PAGE_SIZE, 'AWAITING_SUPERVISOR_REVIEW').subscribe({
      next: (page) => this.items.set(page.items),
      error: (response: HttpErrorResponse) => {
        this.items.set([]);
        if (response.status !== 401 && response.status !== 403) {
          this.loadError.set(this.fallbackMessage(response));
        }
      },
    });
  }

  protected toggleSummary(workOrderId: string): void {
    if (this.expandedId() === workOrderId) {
      this.expandedId.set(null);
      this.summary.set(null);
      this.summaryError.set(null);
      return;
    }

    this.expandedId.set(workOrderId);
    this.summary.set(null);
    this.summaryError.set(null);

    this.workSummaries.get(workOrderId).subscribe({
      next: (loaded) => this.summary.set(loaded),
      error: (response: HttpErrorResponse) => this.summaryError.set(this.fallbackMessage(response)),
    });
  }

  protected onSubmitForAcceptance(item: WorkOrder): void {
    this.submittingId.set(item.workOrderId);
    this.itemError.set(null);

    this.workSummaries.submitForAcceptance(item.workOrderId, `"${item.rowVersion}"`).subscribe({
      next: () => {
        this.submittingId.set(null);
        this.items.set(this.items().filter((existing) => existing.workOrderId !== item.workOrderId));
      },
      error: (response: HttpErrorResponse) => {
        this.submittingId.set(null);
        this.itemError.set(this.describe(response));

        if (response.status === 404 || response.status === 409) {
          // The Work Order changed or is gone: show the real list instead of a stale row.
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
        return 'You do not have permission to perform this action.';
      case 404:
        return 'This Work Order was not found, or it is no longer awaiting review.';
      case 409:
        return problem?.code === 'STATE_CONFLICT' && problem.detail
          ? problem.detail
          : 'This Work Order was changed by another request. The latest list is shown above.';
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
