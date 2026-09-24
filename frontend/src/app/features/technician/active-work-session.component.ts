import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { REPAIR_OUTCOME_CODES } from '../work-orders/work-summary.models';
import { WorkSummaryService } from '../work-orders/work-summary.service';
import { WorkSession } from './work-session.models';
import { WorkSessionService } from './work-session.service';

const REASON_MAX_LENGTH = 1000;
const SUMMARY_TEXT_MAX_LENGTH = 4000;

/**
 * S3-002/S3-003/S3-004: the Technician's own current Work Session, with Pause, Resume and Check-out controls.
 * Pause shows only for a `CHECKED_IN` session (the only state ST-WS-002 allows) and opens a dialog that requires
 * a reason — blank or whitespace-only input keeps the confirm button disabled, but that is convenience only: the
 * backend is the authority and rejects a blank reason with 422. Resume shows only for a `PAUSED` session
 * (ST-WS-003); Check-out shows only for a `CHECKED_IN` session (ST-WS-004), alongside Pause. Both act immediately
 * — neither has a reason field, so neither opens a dialog. Every request carries only what its action needs
 * (Pause: the reason; Resume and Check-out: nothing); status and time always come from the backend. Per the
 * resolved Portfolio Project Owner scope decision, Check-out collects no summary/outcome/evidence — that is a
 * later ticket (`docs/13` §4.14). Once CHECKED_OUT, a "Submit Work Summary" button opens a second dialog
 * requiring both `summaryText` and `repairOutcomeCode` (WSM-API-001; `docs/13` §4.15); its If-Match is the Work
 * Order's own token (`workOrderRowVersion`), not the session's, since the resource being moved is the Work
 * Order — a Technician has no other way to reach that token (`GET /work-orders/{id}` excludes TECHNICIAN).
 * `repairOutcomeCode` is a closed six-value allowlist (`docs/13` §4.15 Decision 5) — offered as a `<select>`
 * from {@link REPAIR_OUTCOME_CODES}, never free text, since the backend rejects anything else with 422.
 */
@Component({
  selector: 'app-active-work-session',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (session(); as current) {
      <section aria-labelledby="active-session-heading">
        <h2 id="active-session-heading">Active Work Session</h2>
        <dl>
          <dt>WO No.</dt>
          <dd>{{ current.workOrderNo }}</dd>
          <dt>Site Code</dt>
          <dd>{{ current.siteCode ?? '—' }}</dd>
          <dt>Equipment Code</dt>
          <dd>{{ current.equipmentCode ?? '—' }}</dd>
          <dt>Status</dt>
          <dd>{{ current.status }}</dd>
          <dt>Checked in</dt>
          <dd>{{ current.checkInAt }}</dd>
          @if (current.status === 'PAUSED' && current.pauses.length > 0) {
            <dt>Paused since</dt>
            <dd>{{ current.pauses[0].pausedAt }}</dd>
            <dt>Pause reason</dt>
            <dd>{{ current.pauses[0].pauseReason }}</dd>
          }
        </dl>

        @if (current.status === 'CHECKED_IN') {
          <button type="button" [disabled]="submitting()" (click)="openDialog()">Pause</button>
          <button type="button" [disabled]="submitting()" (click)="onCheckOut(current)">Check-out</button>
        }
        @if (current.status === 'PAUSED') {
          <button type="button" [disabled]="submitting()" (click)="onResume(current)">Resume</button>
        }
        @if (current.status === 'CHECKED_OUT') {
          <button type="button" [disabled]="submitting()" (click)="openSummaryDialog()">Submit Work Summary</button>
        }
      </section>

      @if (dialogOpen()) {
        <div role="dialog" aria-modal="true" aria-labelledby="pause-dialog-title">
          <h3 id="pause-dialog-title">Pause Work Session</h3>
          <form (submit)="onPause($event, current)">
            <label>
              Reason for pausing (required)
              <textarea
                #reason
                required
                [attr.maxlength]="maxLength"
                [value]="reasonText()"
                (input)="reasonText.set(reason.value)"
              ></textarea>
            </label>
            @if (dialogError()) {
              <p role="alert">{{ dialogError() }}</p>
            }
            <button type="submit" [disabled]="!canSubmit()">Confirm Pause</button>
            <button type="button" [disabled]="submitting()" (click)="closeDialog()">Cancel</button>
          </form>
        </div>
      }

      @if (summaryDialogOpen()) {
        <div role="dialog" aria-modal="true" aria-labelledby="summary-dialog-title">
          <h3 id="summary-dialog-title">Submit Work Summary</h3>
          <form (submit)="onSubmitWorkSummary($event, current)">
            <label>
              Summary (required)
              <textarea
                #summary
                required
                [attr.maxlength]="summaryMaxLength"
                [value]="summaryText()"
                (input)="summaryText.set(summary.value)"
              ></textarea>
            </label>
            <label>
              Repair outcome (required)
              <select #outcome required [value]="repairOutcomeCode()" (change)="repairOutcomeCode.set(outcome.value)">
                <option value="" disabled>Select a repair outcome</option>
                @for (item of repairOutcomeCodes; track item.code) {
                  <option [value]="item.code">{{ item.label }}</option>
                }
              </select>
            </label>
            @if (summaryDialogError()) {
              <p role="alert">{{ summaryDialogError() }}</p>
            }
            <button type="submit" [disabled]="!canSubmitSummary()">Confirm Submit</button>
            <button type="button" [disabled]="submitting()" (click)="closeSummaryDialog()">Cancel</button>
          </form>
        </div>
      }
    }

    @if (actionError()) {
      <p role="alert">{{ actionError() }}</p>
    }
    @if (loadError()) {
      <p role="alert">{{ loadError() }}</p>
    }
  `,
})
export class ActiveWorkSessionComponent {
  private readonly sessions = inject(WorkSessionService);
  private readonly workSummaries = inject(WorkSummaryService);

  protected readonly maxLength = REASON_MAX_LENGTH;
  protected readonly summaryMaxLength = SUMMARY_TEXT_MAX_LENGTH;
  protected readonly repairOutcomeCodes = REPAIR_OUTCOME_CODES;

  protected readonly session = signal<WorkSession | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);

  protected readonly dialogOpen = signal(false);
  protected readonly dialogError = signal<string | null>(null);
  protected readonly reasonText = signal('');
  protected readonly submitting = signal(false);

  protected readonly canSubmit = computed(() => this.reasonText().trim().length > 0 && !this.submitting());

  protected readonly summaryDialogOpen = signal(false);
  protected readonly summaryDialogError = signal<string | null>(null);
  protected readonly summaryText = signal('');
  protected readonly repairOutcomeCode = signal('');

  protected readonly canSubmitSummary = computed(
    () => this.summaryText().trim().length > 0 && this.repairOutcomeCode().trim().length > 0 && !this.submitting(),
  );

  constructor() {
    this.refresh();
  }

  /** Reloads the caller's current session (e.g. after a Check-in elsewhere on the page). */
  refresh(): void {
    this.loadError.set(null);

    this.sessions.current().subscribe({
      next: (session) => this.session.set(session),
      error: (response: HttpErrorResponse) => {
        this.session.set(null);
        // 401/403: the page itself already explains that; only surface genuine failures here.
        if (response.status !== 401 && response.status !== 403) {
          this.loadError.set(this.fallbackMessage(response));
        }
      },
    });
  }

  protected openDialog(): void {
    this.actionError.set(null);
    this.dialogError.set(null);
    this.reasonText.set('');
    this.dialogOpen.set(true);
  }

  protected closeDialog(): void {
    this.dialogOpen.set(false);
    this.dialogError.set(null);
  }

  protected onPause(event: Event, session: WorkSession): void {
    event.preventDefault();
    if (!this.canSubmit()) {
      return;
    }

    this.submitting.set(true);
    this.dialogError.set(null);

    this.sessions.pause(session.workSessionId, `"${session.rowVersion}"`, this.reasonText().trim()).subscribe({
      next: (updated) => {
        this.session.set(updated);
        this.submitting.set(false);
        this.closeDialog();
      },
      error: (response: HttpErrorResponse) => {
        this.submitting.set(false);

        if (response.status === 422) {
          this.dialogError.set('Please enter a reason for the pause.');
          return;
        }

        if (response.status === 404 || response.status === 409) {
          // The session changed or is gone: show the real state instead of leaving a stale Pause button.
          this.closeDialog();
          this.actionError.set(this.describe(response));
          this.refresh();
          return;
        }

        this.dialogError.set(this.describe(response));
      },
    });
  }

  protected onResume(session: WorkSession): void {
    this.submitting.set(true);
    this.actionError.set(null);

    this.sessions.resume(session.workSessionId, `"${session.rowVersion}"`).subscribe({
      next: (updated) => {
        this.session.set(updated);
        this.submitting.set(false);
      },
      error: (response: HttpErrorResponse) => {
        this.submitting.set(false);
        this.actionError.set(this.describe(response));

        if (response.status === 404 || response.status === 409) {
          // The session changed or is gone: show the real state instead of leaving a stale Resume button.
          this.refresh();
        }
      },
    });
  }

  protected onCheckOut(session: WorkSession): void {
    this.submitting.set(true);
    this.actionError.set(null);

    this.sessions.checkOut(session.workSessionId, `"${session.rowVersion}"`).subscribe({
      next: (updated) => {
        this.session.set(updated);
        this.submitting.set(false);
      },
      error: (response: HttpErrorResponse) => {
        this.submitting.set(false);
        this.actionError.set(this.describe(response));

        if (response.status === 404 || response.status === 409) {
          // The session changed or is gone: show the real state instead of leaving a stale Check-out button.
          this.refresh();
        }
      },
    });
  }

  protected openSummaryDialog(): void {
    this.actionError.set(null);
    this.summaryDialogError.set(null);
    this.summaryText.set('');
    this.repairOutcomeCode.set('');
    this.summaryDialogOpen.set(true);
  }

  protected closeSummaryDialog(): void {
    this.summaryDialogOpen.set(false);
    this.summaryDialogError.set(null);
  }

  protected onSubmitWorkSummary(event: Event, session: WorkSession): void {
    event.preventDefault();
    if (!this.canSubmitSummary()) {
      return;
    }

    this.submitting.set(true);
    this.summaryDialogError.set(null);

    this.workSummaries
      .submit(session.workOrderId, `"${session.workOrderRowVersion}"`, {
        summaryText: this.summaryText().trim(),
        repairOutcomeCode: this.repairOutcomeCode().trim(),
      })
      .subscribe({
        next: () => {
          this.submitting.set(false);
          this.closeSummaryDialog();
          // The session is no longer "current" once its Work Summary is submitted — same as after any
          // Check-out reload, `GET /work-sessions/current` excludes CHECKED_OUT sessions.
          this.refresh();
        },
        error: (response: HttpErrorResponse) => {
          this.submitting.set(false);

          if (response.status === 422) {
            this.summaryDialogError.set('Please fill in both the summary and the repair outcome code.');
            return;
          }

          if (response.status === 404 || response.status === 409) {
            this.closeSummaryDialog();
            this.actionError.set(this.describe(response));
            this.refresh();
            return;
          }

          this.summaryDialogError.set(this.describe(response));
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
        return 'This Work Session was not found, or it no longer belongs to you.';
      case 409:
        // STATE_CONFLICT carries an understandable server message (e.g. "already paused"); a stale token keeps the generic hint.
        return problem?.code === 'STATE_CONFLICT' && problem.detail
          ? problem.detail
          : 'This Work Session was changed by another request. The latest state is shown above.';
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
