import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { WorkSession } from './work-session.models';
import { WorkSessionService } from './work-session.service';

const REASON_MAX_LENGTH = 1000;

/**
 * S3-002: the Technician's own current Work Session, with a Pause control. The Pause button shows only for a
 * `CHECKED_IN` session (the only state ST-WS-002 allows); a `PAUSED` session shows what was recorded and no
 * button (Resume is a later ticket). Pausing opens a dialog that requires a reason — blank or whitespace-only
 * input keeps the confirm button disabled, but that is convenience only: the backend is the authority and
 * rejects a blank reason with 422. The request carries only the reason; status and time come from the backend.
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

  protected readonly maxLength = REASON_MAX_LENGTH;

  protected readonly session = signal<WorkSession | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);

  protected readonly dialogOpen = signal(false);
  protected readonly dialogError = signal<string | null>(null);
  protected readonly reasonText = signal('');
  protected readonly submitting = signal(false);

  protected readonly canSubmit = computed(() => this.reasonText().trim().length > 0 && !this.submitting());

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
