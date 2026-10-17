import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import {
  REASON_MAX_LENGTH,
  RequestTimeCorrectionBody,
  TERMINAL_WORK_ORDER_STATUSES,
  TIMESTAMP_TYPES,
  TimeCorrection,
  TimeCorrectionTargetPause,
  TimeCorrectionTargetSession,
  TimeCorrectionTargets,
  TimestampType,
  timestampTypeLabel,
} from './time-correction.models';
import { TimeCorrectionService } from './time-correction.service';
import {
  datetimeLocalToUtcIso,
  formatLocal,
  formatUtc,
  isAfter,
  sameInstant,
  utcToDatetimeLocal,
} from './time-correction.time';

type LoadState = 'loading' | 'ready' | 'unauthorized' | 'forbidden' | 'notFound' | 'failed';
type Field = 'session' | 'timestampType' | 'workSessionPauseId' | 'newValue' | 'reason';

/**
 * Request a Time Correction (UC-TIME-001; TIME-API-001; `docs/13` §4.29; `docs/17` §1A rows 1, 7, 12, 17). Embedded for the roles that may
 * request — the Technician (their own sessions) and the Team Lead (sessions within their Site scope) — and driven by TIME-API-ADD-002, which
 * returns only the sessions of ONE Work Order inside the caller's request scope, with the pause ids PAUSE_START/RESUME must name.
 *
 * - Four types; PAUSE_START and RESUME select the exact pause (RESUME only among closed ones), CHECK_IN and CHECK_OUT send none.
 * - The current value comes from the server; the new value is entered as UTC (labeled), its browser-local equivalent is shown, and it is sent
 *   with an explicit `Z`. The old value, status and requester are never sent.
 * - The checks here (required fields, not in the future, not equal to the current value, reason length, terminal Work Order) are convenience
 *   that mirror the backend; the backend is authoritative and its 422/409/404/403 are shown as they come, field errors on their fields.
 * - One submit at a time (a second click is ignored while the call is in flight); a failure is shown, never retried.
 */
@Component({
  selector: 'app-time-correction-request',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section aria-labelledby="time-correction-request-heading">
      <h3 id="time-correction-request-heading">Request a time correction</h3>

      @switch (loadState()) {
        @case ('loading') {
          <p>Loading work sessions…</p>
        }
        @case ('unauthorized') {
          <p role="alert">You are not signed in, or your session has expired. Please sign in again.</p>
        }
        @case ('forbidden') {
          <p role="alert">Your role cannot request time corrections.</p>
        }
        @case ('notFound') {
          <p>No work session of this Work Order is available for you to correct.</p>
        }
        @case ('failed') {
          <p role="alert">{{ loadError() }}</p>
          <button type="button" (click)="load()">Try again</button>
        }
        @case ('ready') {
          @if (targets(); as loaded) {
            <p>
              Work Order {{ loaded.workOrderNo }} — status {{ loaded.workOrderStatus }}
              @if (loaded.siteCode) {
                — site {{ loaded.siteCode }}
              }
            </p>

            @if (terminal()) {
              <p role="alert">
                A time correction cannot be requested for a {{ loaded.workOrderStatus }} Work Order. A request that is
                already pending can still be rejected by the Supervisor.
              </p>
            }

            <p>
              Times are shown in UTC, with your local time next to them. Enter the new time in UTC.
            </p>

            <form (submit)="submit($event)" novalidate>
              <label>
                Work session
                <select
                  #sessionSelect
                  [disabled]="submitting() || terminal()"
                  [attr.aria-invalid]="issue('session') ? 'true' : null"
                  (change)="selectSession(sessionSelect.value)"
                >
                  @if (loaded.sessions.length !== 1) {
                    <option value="" [selected]="selectedSessionId() === ''">Select a work session</option>
                  }
                  @for (item of loaded.sessions; track item.workSessionId) {
                    <option [value]="item.workSessionId" [selected]="item.workSessionId === selectedSessionId()">
                      {{ describeSession(item, $index) }}
                    </option>
                  }
                </select>
              </label>

              @if (session(); as chosen) {
                <dl>
                  <dt>Checked in</dt>
                  <dd>{{ formatUtc(chosen.checkInAt) }} ({{ formatLocal(chosen.checkInAt) }})</dd>
                  <dt>Checked out</dt>
                  <dd>
                    @if (chosen.checkOutAt) {
                      {{ formatUtc(chosen.checkOutAt) }} ({{ formatLocal(chosen.checkOutAt) }})
                    } @else {
                      not yet
                    }
                  </dd>
                  @for (pause of chosen.pauses; track pause.workSessionPauseId; let index = $index) {
                    <dt>Pause {{ index + 1 }}</dt>
                    <dd>{{ describePause(pause) }}</dd>
                  }
                </dl>
              }

              <label>
                Time to correct
                <select
                  #typeSelect
                  [disabled]="submitting() || terminal() || !session()"
                  [attr.aria-invalid]="issue('timestampType') ? 'true' : null"
                  (change)="selectType(typeSelect.value)"
                >
                  <option value="" [selected]="typeCode() === ''">Select the time to correct</option>
                  @for (option of typeOptions(); track option.code) {
                    <option [value]="option.code" [selected]="option.code === typeCode()" [disabled]="!option.available">
                      {{ option.label }}{{ option.available ? '' : ' — ' + option.unavailableReason }}
                    </option>
                  }
                </select>
              </label>
              @if (issue('timestampType'); as message) {
                <p role="alert">{{ message }}</p>
              }

              @if (needsPause()) {
                <label>
                  Pause
                  <select
                    #pauseSelect
                    [disabled]="submitting() || terminal()"
                    [attr.aria-invalid]="issue('workSessionPauseId') ? 'true' : null"
                    (change)="selectPause(pauseSelect.value)"
                  >
                    <option value="" [selected]="pauseId() === ''">Select the pause</option>
                    @for (pause of pauseChoices(); track pause.workSessionPauseId) {
                      <option [value]="pause.workSessionPauseId" [selected]="pause.workSessionPauseId === pauseId()">
                        {{ describePause(pause) }}
                      </option>
                    }
                  </select>
                </label>
                @if (issue('workSessionPauseId'); as message) {
                  <p role="alert">{{ message }}</p>
                }
              }

              @if (typeCode() !== '') {
                <p>
                  Current {{ typeLabel().toLowerCase() }}:
                  @if (currentValue(); as current) {
                    <time [attr.datetime]="current" [title]="'UTC ' + current">{{ formatUtc(current) }}</time>
                    ({{ formatLocal(current) }})
                  } @else {
                    none
                  }
                </p>
              }

              <label>
                New time (UTC)
                <input
                  #newValueInput
                  type="datetime-local"
                  step="1"
                  required
                  [disabled]="submitting() || terminal() || typeCode() === ''"
                  [value]="newValueText()"
                  [attr.aria-invalid]="issue('newValue') ? 'true' : null"
                  (input)="setNewValue(newValueInput.value)"
                />
              </label>
              @if (proposedIso(); as proposed) {
                <p>
                  That is <time [attr.datetime]="proposed">{{ formatUtc(proposed) }}</time>, which is
                  {{ formatLocal(proposed) }} in your local time.
                </p>
              }
              @if (issue('newValue'); as message) {
                <p role="alert">{{ message }}</p>
              }

              <label>
                Reason (required)
                <textarea
                  #reasonInput
                  required
                  [attr.maxlength]="reasonMaxLength"
                  [disabled]="submitting() || terminal()"
                  [value]="reasonText()"
                  [attr.aria-invalid]="issue('reason') ? 'true' : null"
                  (input)="setReason(reasonInput.value)"
                ></textarea>
              </label>
              @if (issue('reason'); as message) {
                <p role="alert">{{ message }}</p>
              }

              @if (formError()) {
                <p role="alert">{{ formError() }}</p>
              }

              <button type="submit" [disabled]="!canSubmit()">Send request</button>
            </form>

            @if (created(); as sent) {
              <p role="status">
                Request sent for Supervisor review: {{ timestampTypeLabel(sent.timestampType) }} from
                {{ formatUtc(sent.oldValue) }} to {{ formatUtc(sent.newValue) }} ({{ sent.status }}). Nothing changes until a
                Supervisor approves it.
              </p>
            }
          }
        }
      }
    </section>
  `,
})
export class TimeCorrectionRequestComponent implements OnInit {
  private readonly corrections = inject(TimeCorrectionService);

  readonly workOrderId = input.required<string>();
  /** Emitted after the server accepted a request (201). */
  readonly requested = output<TimeCorrection>();

  protected readonly reasonMaxLength = REASON_MAX_LENGTH;
  protected readonly formatUtc = formatUtc;
  protected readonly formatLocal = formatLocal;
  protected readonly timestampTypeLabel = timestampTypeLabel;

  protected readonly loadState = signal<LoadState>('loading');
  protected readonly loadError = signal<string | null>(null);
  protected readonly targets = signal<TimeCorrectionTargets | null>(null);

  protected readonly selectedSessionId = signal('');
  protected readonly typeCode = signal<TimestampType | ''>('');
  protected readonly pauseId = signal('');
  protected readonly newValueText = signal('');
  protected readonly reasonText = signal('');
  private readonly touched = signal<ReadonlySet<Field>>(new Set());

  protected readonly submitting = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly serverIssues = signal<Partial<Record<Field, string>>>({});
  protected readonly created = signal<TimeCorrection | null>(null);

  protected readonly terminal = computed(() => {
    const loaded = this.targets();
    return loaded !== null && TERMINAL_WORK_ORDER_STATUSES.includes(loaded.workOrderStatus);
  });

  protected readonly session = computed<TimeCorrectionTargetSession | null>(
    () => this.targets()?.sessions.find((item) => item.workSessionId === this.selectedSessionId()) ?? null,
  );

  protected readonly typeOption = computed(() => TIMESTAMP_TYPES.find((option) => option.code === this.typeCode()) ?? null);
  protected readonly needsPause = computed(() => this.typeOption()?.needsPause === true);
  protected readonly typeLabel = computed(() => this.typeOption()?.label ?? '');

  /** The four types with what this session offers; unavailable ones stay visible with the reason (the backend decides regardless). */
  protected readonly typeOptions = computed(() => {
    const chosen = this.session();
    return TIMESTAMP_TYPES.map((option) => {
      let unavailableReason = '';
      if (chosen !== null) {
        if (option.code === 'CHECK_OUT' && chosen.checkOutAt === null) {
          unavailableReason = 'the session is not checked out yet';
        } else if (option.code === 'PAUSE_START' && chosen.pauses.length === 0) {
          unavailableReason = 'the session has no pause';
        } else if (option.code === 'RESUME' && !chosen.pauses.some((pause) => pause.resumedAt !== null)) {
          unavailableReason = 'the session has no resumed pause';
        }
      }

      return { ...option, available: unavailableReason === '', unavailableReason };
    });
  });

  /** PAUSE_START may target any pause; RESUME only a pause that was resumed. */
  protected readonly pauseChoices = computed<TimeCorrectionTargetPause[]>(() => {
    const chosen = this.session();
    if (chosen === null) {
      return [];
    }

    return this.typeCode() === 'RESUME' ? chosen.pauses.filter((pause) => pause.resumedAt !== null) : chosen.pauses;
  });

  protected readonly currentValue = computed<string | null>(() => {
    const chosen = this.session();
    if (chosen === null) {
      return null;
    }

    switch (this.typeCode()) {
      case 'CHECK_IN':
        return chosen.checkInAt;
      case 'CHECK_OUT':
        return chosen.checkOutAt;
      case 'PAUSE_START':
        return chosen.pauses.find((pause) => pause.workSessionPauseId === this.pauseId())?.pausedAt ?? null;
      case 'RESUME':
        return chosen.pauses.find((pause) => pause.workSessionPauseId === this.pauseId())?.resumedAt ?? null;
      default:
        return null;
    }
  });

  protected readonly proposedIso = computed(() => datetimeLocalToUtcIso(this.newValueText()));

  /** Client-side findings, shown for fields the user has touched; the backend remains authoritative. */
  private readonly findings = computed<Partial<Record<Field, string>>>(() => {
    const found: Partial<Record<Field, string>> = {};
    if (this.session() === null) {
      found.session = 'Select a work session.';
    }

    if (this.typeCode() === '') {
      found.timestampType = 'Select the time to correct.';
    } else if (this.needsPause() && this.pauseId() === '') {
      found.workSessionPauseId = 'Select the pause.';
    }

    const text = this.newValueText().trim();
    if (text === '') {
      found.newValue = 'Enter the new time (UTC).';
    } else if (this.proposedIso() === null) {
      found.newValue = 'Enter a complete, valid date and time.';
    } else if (isAfter(this.proposedIso()!)) {
      found.newValue = 'The new time cannot be in the future.';
    } else if (this.typeCode() !== '' && this.currentValue() === null && (!this.needsPause() || this.pauseId() !== '')) {
      found.newValue = 'There is no current time to correct for this selection.';
    } else if (sameInstant(this.proposedIso(), this.currentValue())) {
      found.newValue = 'The new time is the same as the current time.';
    }

    const reason = this.reasonText().trim();
    if (reason === '') {
      found.reason = 'A reason is required.';
    } else if (reason.length > REASON_MAX_LENGTH) {
      found.reason = `The reason can be at most ${REASON_MAX_LENGTH} characters.`;
    }

    return found;
  });

  protected readonly canSubmit = computed(
    () => !this.submitting() && !this.terminal() && this.session() !== null && Object.keys(this.findings()).length === 0,
  );

  ngOnInit(): void {
    this.load();
  }

  /** Loads (or reloads) the correctable sessions of the Work Order. */
  load(): void {
    this.corrections.requestTargets(this.workOrderId()).subscribe({
      next: (loaded) => {
        this.targets.set(loaded);
        this.loadState.set('ready');
        // Keep a valid choice across a reload; a single session is selected for the user.
        if (!loaded.sessions.some((item) => item.workSessionId === this.selectedSessionId())) {
          this.selectedSessionId.set(loaded.sessions.length === 1 ? loaded.sessions[0].workSessionId : '');
          this.typeCode.set('');
          this.pauseId.set('');
          this.newValueText.set('');
        }
      },
      error: (response: HttpErrorResponse) => {
        this.targets.set(null);
        switch (response.status) {
          case 401:
            this.loadState.set('unauthorized');
            break;
          case 403:
            this.loadState.set('forbidden');
            break;
          case 404:
            this.loadState.set('notFound');
            break;
          default:
            this.loadError.set(
              response.status === 0
                ? 'Could not reach the server. Check your connection and try again.'
                : 'The work sessions could not be loaded. Please try again.',
            );
            this.loadState.set('failed');
        }
      },
    });
  }

  /** Numbered and with its pause count, because several sessions of one Work Order can share a check-in time (a Team Lead sees them all). */
  protected describeSession(item: TimeCorrectionTargetSession, index: number): string {
    const pauses = item.pauses.length === 1 ? '1 pause' : `${item.pauses.length} pauses`;
    return `Session ${index + 1} — checked in ${formatUtc(item.checkInAt)} — ${item.status} — ${pauses}`;
  }

  protected describePause(pause: TimeCorrectionTargetPause): string {
    return `Paused ${formatUtc(pause.pausedAt)} → ${pause.resumedAt ? `resumed ${formatUtc(pause.resumedAt)}` : 'still paused'}`;
  }

  /** The message to show for a field: the server's, or a client finding once the field has been touched. */
  protected issue(field: Field): string | null {
    return this.serverIssues()[field] ?? (this.touched().has(field) ? (this.findings()[field] ?? null) : null);
  }

  protected selectSession(sessionId: string): void {
    this.selectedSessionId.set(sessionId);
    this.typeCode.set('');
    this.pauseId.set('');
    this.newValueText.set('');
    this.touch('session');
    this.clearServerIssues();
  }

  protected selectType(code: string): void {
    this.typeCode.set(TIMESTAMP_TYPES.some((option) => option.code === code) ? (code as TimestampType) : '');
    this.pauseId.set('');
    // Start from the current value so only the part that is wrong has to be edited; an unchanged value is refused below.
    this.newValueText.set(this.needsPause() ? '' : utcToDatetimeLocal(this.currentValue()));
    this.touch('timestampType');
    this.clearServerIssues();
  }

  protected selectPause(id: string): void {
    this.pauseId.set(id);
    this.newValueText.set(utcToDatetimeLocal(this.currentValue()));
    this.touch('workSessionPauseId');
    this.clearServerIssues();
  }

  protected setNewValue(text: string): void {
    this.newValueText.set(text);
    this.touch('newValue');
    this.clearServerIssues();
  }

  protected setReason(text: string): void {
    this.reasonText.set(text);
    this.touch('reason');
    this.clearServerIssues();
  }

  protected submit(event: Event): void {
    event.preventDefault();
    const chosen = this.session();
    const newValue = this.proposedIso();
    // A second click while the call is in flight is ignored (the button is also disabled).
    if (this.submitting() || !this.canSubmit() || chosen === null || newValue === null || this.typeCode() === '') {
      return;
    }

    const body: RequestTimeCorrectionBody = {
      timestampType: this.typeCode() as TimestampType,
      newValue,
      reason: this.reasonText().trim(),
      ...(this.needsPause() ? { workSessionPauseId: this.pauseId() } : {}),
    };

    this.submitting.set(true);
    this.formError.set(null);
    this.serverIssues.set({});
    this.created.set(null);

    this.corrections.request(chosen.workSessionId, body).subscribe({
      next: (sent) => {
        this.submitting.set(false);
        this.created.set(sent);
        this.typeCode.set('');
        this.pauseId.set('');
        this.newValueText.set('');
        this.reasonText.set('');
        this.touched.set(new Set());
        this.requested.emit(sent);
        this.load();
      },
      error: (response: HttpErrorResponse) => {
        this.submitting.set(false);
        this.onRequestError(response);
      },
    });
  }

  private onRequestError(response: HttpErrorResponse): void {
    const problem = response.error as { code?: string; detail?: string; errors?: Record<string, string[]> } | null;

    switch (response.status) {
      case 422: {
        const issues: Partial<Record<Field, string>> = {};
        const fields: Record<string, Field> = {
          timestampType: 'timestampType',
          workSessionPauseId: 'workSessionPauseId',
          newValue: 'newValue',
          reason: 'reason',
        };
        for (const [name, messages] of Object.entries(problem?.errors ?? {})) {
          const field = fields[name];
          if (field !== undefined && messages.length > 0) {
            issues[field] = messages[0];
          }
        }

        this.serverIssues.set(issues);
        if (Object.keys(issues).length === 0) {
          this.formError.set(problem?.detail ?? 'The request was not accepted. Please check the values and try again.');
        }

        return;
      }
      case 409:
        // STATE_CONFLICT carries an understandable message (a request is already pending; the Work Order is CLOSED/CANCELLED).
        this.formError.set(
          problem?.code === 'STATE_CONFLICT' && problem.detail
            ? problem.detail
            : 'This work session was changed by another request. The latest state is shown.',
        );
        this.load();
        return;
      case 404:
        this.formError.set('This work session was not found, or it is no longer in your scope.');
        this.load();
        return;
      case 403:
        this.formError.set('You do not have permission to request this time correction.');
        return;
      case 401:
        this.formError.set('You are not signed in, or your session has expired. Please sign in again.');
        return;
      case 0:
        this.formError.set('Could not reach the server. Check your connection and try again.');
        return;
      default:
        this.formError.set('Something went wrong. Please try again.');
    }
  }

  private touch(field: Field): void {
    if (!this.touched().has(field)) {
      this.touched.set(new Set([...this.touched(), field]));
    }
  }

  private clearServerIssues(): void {
    this.serverIssues.set({});
    this.formError.set(null);
  }
}
