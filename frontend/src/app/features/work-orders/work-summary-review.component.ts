import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { WorkOrder } from './work-order.models';
import { WorkOrderService } from './work-order.service';
import { EligibleAcceptanceContact, WorkSummary } from './work-summary.models';
import { WorkSummaryService } from './work-summary.service';

const PAGE_SIZE = 20;

/**
 * Team Lead / Supervisor review of Work Orders awaiting Work Summary review (`docs/13` §4.15/§4.16). Lists every
 * Work Order in `AWAITING_SUPERVISOR_REVIEW` status within the caller's site scope (reuses the existing S2-001
 * list endpoint, unchanged); "View Summary" fetches and shows the submitted `summaryText`/`repairOutcomeCode`
 * inline. "Submit for Acceptance" opens a dialog that fetches the eligible REQUESTER contacts
 * (`ACC-API-ADD-001`) and requires picking one from a `<select>` — never a free-text id — before submitting;
 * even when only one candidate exists it is pre-selected but the caller must still confirm (`docs/13` §4.16
 * Decision 3). Moves the Work Order to `AWAITING_CUSTOMER_ACCEPTANCE` and removes it from this list. Either
 * role may submit for acceptance — there is no separate "Team Lead approves, then Supervisor decides" step.
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

          <button type="button" [disabled]="submitting()" (click)="toggleSummary(item.workOrderId)">
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

          <button type="button" [disabled]="submitting()" (click)="openContactDialog(item)">Submit for Acceptance</button>
        </li>
      }
    </ul>

    @if (dialogItem(); as dialogFor) {
      <div role="dialog" aria-modal="true" aria-labelledby="contact-dialog-title">
        <h3 id="contact-dialog-title">Submit {{ dialogFor.workOrderNo }} for Acceptance</h3>

        @if (contactsLoading()) {
          <p>Loading eligible contacts…</p>
        } @else if (contacts().length === 0) {
          <p role="alert">No eligible Acceptance Contact was found for this Work Order's Site.</p>
        } @else {
          <form (submit)="onSubmitForAcceptance($event, dialogFor)">
            <label>
              Acceptance Contact (required)
              <select #contact required [value]="selectedContactId()" (change)="selectedContactId.set(contact.value)">
                <option value="" disabled>Select a contact</option>
                @for (candidate of contacts(); track candidate.userId) {
                  <option [value]="candidate.userId">{{ candidate.displayName }} ({{ candidate.email }})</option>
                }
              </select>
            </label>
            @if (dialogError()) {
              <p role="alert">{{ dialogError() }}</p>
            }
            <button type="submit" [disabled]="!canSubmit()">Confirm Submit</button>
            <button type="button" [disabled]="submitting()" (click)="closeContactDialog()">Cancel</button>
          </form>
        }
      </div>
    }
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

  protected readonly submitting = signal(false);
  protected readonly itemError = signal<string | null>(null);

  protected readonly dialogItem = signal<WorkOrder | null>(null);
  protected readonly contactsLoading = signal(false);
  protected readonly contacts = signal<EligibleAcceptanceContact[]>([]);
  protected readonly selectedContactId = signal('');
  protected readonly dialogError = signal<string | null>(null);

  protected readonly canSubmit = computed(() => this.selectedContactId().trim().length > 0 && !this.submitting());

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

  protected openContactDialog(item: WorkOrder): void {
    this.itemError.set(null);
    this.dialogError.set(null);
    this.dialogItem.set(item);
    this.selectedContactId.set('');
    this.contacts.set([]);
    this.contactsLoading.set(true);

    this.workSummaries.getEligibleAcceptanceContacts(item.workOrderId).subscribe({
      next: (candidates) => {
        this.contactsLoading.set(false);
        this.contacts.set(candidates);
        // A single eligible candidate is pre-selected for convenience, but the caller must still explicitly
        // confirm by submitting the form — `docs/13` §4.16 Decision 3 requires this even then.
        if (candidates.length === 1) {
          this.selectedContactId.set(candidates[0].userId);
        }
      },
      error: (response: HttpErrorResponse) => {
        this.contactsLoading.set(false);
        this.dialogError.set(this.fallbackMessage(response));
      },
    });
  }

  protected closeContactDialog(): void {
    this.dialogItem.set(null);
    this.dialogError.set(null);
  }

  protected onSubmitForAcceptance(event: Event, item: WorkOrder): void {
    event.preventDefault();
    if (!this.canSubmit()) {
      return;
    }

    this.submitting.set(true);
    this.dialogError.set(null);

    this.workSummaries.submitForAcceptance(item.workOrderId, `"${item.rowVersion}"`, this.selectedContactId()).subscribe({
      next: () => {
        this.submitting.set(false);
        this.closeContactDialog();
        this.items.set(this.items().filter((existing) => existing.workOrderId !== item.workOrderId));
      },
      error: (response: HttpErrorResponse) => {
        this.submitting.set(false);

        if (response.status === 404 || response.status === 409) {
          // The Work Order changed or is gone: show the real list instead of a stale row.
          this.closeContactDialog();
          this.itemError.set(this.describe(response));
          this.refresh();
          return;
        }

        this.dialogError.set(this.describe(response));
      },
    });
  }

  private describe(response: HttpErrorResponse): string {
    const problem = response.error as { code?: string; detail?: string; errors?: Record<string, string[]> } | null;

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
      case 422:
        return problem?.errors ? Object.values(problem.errors).flat().join(' ') : 'The selected contact is no longer eligible.';
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
