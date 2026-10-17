import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { getCurrentUserId } from '../../core/auth/current-user-role';
import { REASON_MAX_LENGTH, TimeCorrection, timestampTypeLabel } from './time-correction.models';
import { TimeCorrectionService } from './time-correction.service';
import { formatLocal, formatUtc } from './time-correction.time';

const PAGE_SIZE = 20;

type LoadState = 'loading' | 'ready' | 'unauthorized' | 'forbidden' | 'failed';

/**
 * Supervisor review of PENDING Time Corrections (UC-TIME-001; TIME-API-ADD-001/002/003; `docs/13` §4.29; `docs/17` §1A rows 3, 4, 5, 13, 17).
 * Lists the caller's Site-scoped pending requests, oldest first, with what the Supervisor needs to decide: Work Order, site/equipment, the
 * timestamp, the current (recorded) and the proposed time in UTC with the local equivalent, the requester's reason (disclosed to the Supervisor
 * by this API contract; the Work Order timeline still hides it) and who asked.
 *
 * - Approve and Reject send the correction's own `rowVersion` from the list item being looked at as `If-Match`. Reject requires a reason.
 * - A request made by the signed-in user cannot be decided by them (no self-decision); the buttons are disabled with the explanation, and the
 *   backend's 403 is the real rule.
 * - One decision at a time: while a call is in flight every decision control is disabled and a second click is ignored. A decision is NEVER
 *   retried. On a conflict (409: stale token, lost race, changed target, chronology, closed/cancelled Work Order) or a vanished request (404) the
 *   list is reloaded from the server, any open reject form is closed, and the Supervisor must decide again explicitly.
 * - Approve is refused by the backend for a CLOSED/CANCELLED Work Order; Reject stays possible. Chronology, Visit completion, SLA, cost and
 *   acceptance are the backend's concern; this screen computes none of them.
 * - Convenience gate only (`supervisorOnlyGuard`); the backend re-checks role, Site scope and token on every call.
 */
@Component({
  selector: 'app-time-correction-review',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2>Time Corrections Awaiting Review</h2>

    @switch (loadState()) {
      @case ('loading') {
        <p>Loading…</p>
      }
      @case ('unauthorized') {
        <p role="alert">You are not signed in, or your session has expired. Please sign in again.</p>
      }
      @case ('forbidden') {
        <p role="alert">Your role cannot review time corrections.</p>
      }
      @case ('failed') {
        <p role="alert">{{ loadError() }}</p>
        <button type="button" (click)="refresh()">Try again</button>
      }
      @case ('ready') {
        @if (items().length === 0) {
          <p>No time corrections are awaiting review.</p>
        }
      }
    }

    @if (notice()) {
      <p role="status">{{ notice() }}</p>
    }
    @if (actionError()) {
      <p role="alert">{{ actionError() }}</p>
    }

    @if (loadState() === 'ready' && items().length > 0) {
      <ul>
        @for (item of items(); track item.timeCorrectionId) {
          <li>
            <h3>
              <a [routerLink]="['/work-orders', item.workOrderId]">{{ item.workOrderNo }}</a>
              — {{ typeLabel(item.timestampType) }}
            </h3>
            <dl>
              <dt>Site</dt>
              <dd>{{ item.siteCode ?? '—' }}</dd>
              <dt>Equipment</dt>
              <dd>{{ item.equipmentCode ?? '—' }}</dd>
              @if (item.workSessionPauseId) {
                <dt>Pause</dt>
                <dd>{{ shortId(item.workSessionPauseId) }}</dd>
              }
              <dt>Current time</dt>
              <dd>
                <time [attr.datetime]="item.oldValue" [title]="'UTC ' + item.oldValue">{{ formatUtc(item.oldValue) }}</time>
                ({{ formatLocal(item.oldValue) }})
              </dd>
              <dt>Requested time</dt>
              <dd>
                <time [attr.datetime]="item.newValue" [title]="'UTC ' + item.newValue">{{ formatUtc(item.newValue) }}</time>
                ({{ formatLocal(item.newValue) }})
              </dd>
              <dt>Reason</dt>
              <dd>{{ item.reason }}</dd>
              <dt>Requested by</dt>
              <dd>{{ isOwn(item) ? 'you' : shortId(item.requestedBy) }}</dd>
            </dl>

            @if (isOwn(item)) {
              <p role="note">You requested this correction, so you cannot approve or reject it.</p>
            }

            <button type="button" [disabled]="busy() || isOwn(item)" (click)="approve(item)">Approve</button>
            <button type="button" [disabled]="busy() || isOwn(item)" (click)="openReject(item)">Reject</button>

            @if (rejectingId() === item.timeCorrectionId) {
              <form (submit)="reject($event, item)" novalidate>
                <label>
                  Reason for rejecting (required)
                  <textarea
                    #rejectInput
                    required
                    [attr.maxlength]="reasonMaxLength"
                    [disabled]="busy()"
                    [value]="rejectReason()"
                    [attr.aria-invalid]="rejectIssue() ? 'true' : null"
                    (input)="setRejectReason(rejectInput.value)"
                  ></textarea>
                </label>
                @if (rejectIssue()) {
                  <p role="alert">{{ rejectIssue() }}</p>
                }
                <button type="submit" [disabled]="!canReject()">Confirm Reject</button>
                <button type="button" [disabled]="busy()" (click)="closeReject()">Cancel</button>
              </form>
            }
          </li>
        }
      </ul>

      <div>
        <button type="button" [disabled]="busy() || page() <= 1" (click)="goToPage(page() - 1)">Previous</button>
        <span>Page {{ page() }} of {{ totalPages() }} ({{ totalCount() }} total)</span>
        <button type="button" [disabled]="busy() || page() >= totalPages()" (click)="goToPage(page() + 1)">Next</button>
      </div>
    }
  `,
})
export class TimeCorrectionReviewComponent {
  private readonly corrections = inject(TimeCorrectionService);
  private readonly currentUserId = getCurrentUserId();

  protected readonly reasonMaxLength = REASON_MAX_LENGTH;
  protected readonly formatUtc = formatUtc;
  protected readonly formatLocal = formatLocal;
  protected readonly typeLabel = timestampTypeLabel;

  protected readonly loadState = signal<LoadState>('loading');
  protected readonly loadError = signal<string | null>(null);
  protected readonly items = signal<TimeCorrection[]>([]);
  protected readonly page = signal(1);
  protected readonly totalCount = signal(0);
  protected readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / PAGE_SIZE)));

  /** True while a decision call is in flight: every decision control is disabled and a second click is ignored. */
  protected readonly busy = signal(false);
  protected readonly notice = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);

  protected readonly rejectingId = signal<string | null>(null);
  protected readonly rejectReason = signal('');
  protected readonly rejectIssue = signal<string | null>(null);
  protected readonly canReject = computed(() => !this.busy() && this.rejectReason().trim().length > 0);

  constructor() {
    this.refresh();
  }

  /** Reloads the current page from the server (the list is the only source of the row versions). */
  refresh(): void {
    this.loadFrom(this.page());
  }

  protected goToPage(page: number): void {
    if (this.busy() || page < 1 || page > this.totalPages()) {
      return;
    }

    this.closeReject();
    this.loadFrom(page);
  }

  protected isOwn(item: TimeCorrection): boolean {
    return this.currentUserId !== null && item.requestedBy === this.currentUserId;
  }

  protected shortId(id: string): string {
    return id.slice(0, 8);
  }

  protected openReject(item: TimeCorrection): void {
    if (this.busy() || this.isOwn(item)) {
      return;
    }

    this.rejectingId.set(item.timeCorrectionId);
    this.rejectReason.set('');
    this.rejectIssue.set(null);
  }

  protected closeReject(): void {
    this.rejectingId.set(null);
    this.rejectReason.set('');
    this.rejectIssue.set(null);
  }

  protected setRejectReason(text: string): void {
    this.rejectReason.set(text);
    this.rejectIssue.set(null);
  }

  protected approve(item: TimeCorrection): void {
    if (this.busy() || this.isOwn(item)) {
      return;
    }

    this.begin();
    this.corrections.approve(item.timeCorrectionId, item.rowVersion).subscribe({
      next: (decided) => this.decided(`Approved: ${describeType(decided)} of ${decided.workOrderNo}.`),
      error: (response: HttpErrorResponse) => this.failed(item, response, 'approve'),
    });
  }

  protected reject(event: Event, item: TimeCorrection): void {
    event.preventDefault();
    const reason = this.rejectReason().trim();
    if (this.busy() || this.isOwn(item) || reason.length === 0) {
      return;
    }

    this.begin();
    this.corrections.reject(item.timeCorrectionId, item.rowVersion, reason).subscribe({
      next: (decided) => this.decided(`Rejected: ${describeType(decided)} of ${decided.workOrderNo}. No time was changed.`),
      error: (response: HttpErrorResponse) => this.failed(item, response, 'reject'),
    });
  }

  private begin(): void {
    this.busy.set(true);
    this.notice.set(null);
    this.actionError.set(null);
    this.rejectIssue.set(null);
  }

  private decided(message: string): void {
    this.busy.set(false);
    this.closeReject();
    this.notice.set(message);
    this.refresh();
  }

  /** Never retries. Anything but a field error reloads the list and closes the reject form, so the next decision is a new, explicit one. */
  private failed(item: TimeCorrection, response: HttpErrorResponse, action: 'approve' | 'reject'): void {
    this.busy.set(false);
    const problem = response.error as { code?: string; detail?: string } | null;

    if (response.status === 422 && action === 'reject') {
      this.rejectIssue.set('A decision reason is required.');
      return;
    }

    switch (response.status) {
      case 409:
        this.actionError.set(
          problem?.code === 'STATE_CONFLICT' && problem.detail
            ? `${problem.detail}${action === 'approve' ? ' If the Work Order is closed or cancelled you can still reject this request.' : ''} The list has been reloaded; decide again if it is still pending.`
            : 'This request was changed by someone else since you opened the list. The list has been reloaded; review it again and decide again.',
        );
        break;
      case 404:
        this.actionError.set('This request no longer exists, or it is outside your Site scope. The list has been reloaded.');
        break;
      case 403:
        this.actionError.set(
          this.isOwn(item)
            ? 'You requested this correction, so you cannot decide it.'
            : 'You do not have permission to decide this request.',
        );
        break;
      case 401:
        this.actionError.set('You are not signed in, or your session has expired. Please sign in again.');
        return;
      case 0:
        this.actionError.set('Could not reach the server. Check your connection; the request was not retried.');
        return;
      default:
        this.actionError.set('Something went wrong. The request was not retried; reload the list and decide again.');
    }

    this.closeReject();
    this.refresh();
  }

  private loadFrom(page: number): void {
    this.corrections.listPending(page, PAGE_SIZE).subscribe({
      next: (result) => {
        // The last row of a later page was decided: step back instead of showing an empty page.
        if (result.items.length === 0 && result.totalCount > 0 && page > 1) {
          this.loadFrom(Math.max(1, Math.ceil(result.totalCount / PAGE_SIZE)));
          return;
        }

        this.items.set(result.items);
        this.page.set(result.page);
        this.totalCount.set(result.totalCount);
        this.loadState.set('ready');
      },
      error: (response: HttpErrorResponse) => {
        this.items.set([]);
        switch (response.status) {
          case 401:
            this.loadState.set('unauthorized');
            break;
          case 403:
            this.loadState.set('forbidden');
            break;
          default:
            this.loadError.set(
              response.status === 0
                ? 'Could not reach the server. Check your connection and try again.'
                : 'The time corrections could not be loaded. Please try again.',
            );
            this.loadState.set('failed');
        }
      },
    });
  }
}

function describeType(correction: TimeCorrection): string {
  return timestampTypeLabel(correction.timestampType).toLowerCase();
}
