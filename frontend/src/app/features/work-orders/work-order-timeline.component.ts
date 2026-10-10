import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { timelineActionLabel, timelineDetailLabel } from './work-order-timeline.labels';
import { AuditActorKind, AuditTimelineActor, AuditTimelineDetailValue, AuditTimelineItem } from './work-order-timeline.models';
import { WorkOrderService } from './work-order.service';

type TimelineError = 'forbidden' | 'notFound' | 'badCursor' | 'failed';

/**
 * Work Order Timeline (UC-WO-002; AUD-API-001; `docs/15` §4), shown in Work Order Detail. Read-only, newest first.
 *
 * - Keyset "Load more": the opaque `nextCursor` is only echoed back; items are appended and de-duplicated by `auditId`.
 * - States: loading, empty, error with Retry, 403 / 404 messages — a failure never hides the rest of the page.
 * - Reloads from the first page whenever `refreshKey` (the Work Order's `rowVersion`) changes, ignoring stale responses.
 * - `occurredAt` is UTC: `<time datetime>` and its title keep UTC, the visible text is the browser-local time.
 * - No `localStorage`/`sessionStorage`, no raw JSON: `details` are allowlisted scalar values from the server.
 * UX only — the backend re-checks role, scope and every cursor on each request.
 */
@Component({
  selector: 'app-work-order-timeline',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    .timeline-list {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .timeline-item {
      padding: 0.5rem 0;
      border-bottom: 1px solid #ddd;
    }
    .timeline-item:focus {
      outline: 2px solid #1a56db;
      outline-offset: 2px;
    }
    .timeline-meta {
      display: flex;
      flex-wrap: wrap;
      gap: 0.25rem 1rem;
    }
    .timeline-details {
      margin: 0.25rem 0 0;
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 0 0.75rem;
    }
    .timeline-details dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
      white-space: nowrap;
    }
    @media (max-width: 40rem) {
      .timeline-details {
        grid-template-columns: 1fr;
      }
    }
  `,
  template: `
    <section aria-labelledby="work-order-timeline-heading" [attr.aria-busy]="initialLoading() || loadingMore()">
      <h2 id="work-order-timeline-heading">Timeline</h2>

      @if (initialLoading()) {
        <p role="status">Loading timeline…</p>
      } @else if (items().length === 0 && error() !== null) {
        <p role="alert">{{ errorMessage() }}</p>
        @if (error() === 'failed' || error() === 'badCursor') {
          <button type="button" [disabled]="initialLoading()" (click)="reload()">Retry</button>
        }
      } @else if (items().length === 0) {
        <p>No activity recorded yet.</p>
      } @else {
        <ol class="timeline-list" aria-label="Work Order activity, newest first">
          @for (item of items(); track item.auditId) {
            <li class="timeline-item" tabindex="-1">
              <div class="timeline-meta">
                <strong>{{ label(item.actionCode) }}</strong>
                <time [attr.datetime]="item.occurredAt" [title]="'UTC ' + item.occurredAt">{{ local(item.occurredAt) }}</time>
              </div>
              @if (item.fromState !== null || item.toState !== null) {
                <div>{{ item.fromState ?? '—' }} &rarr; {{ item.toState ?? '—' }}</div>
              }
              <div>By {{ actorText(item.actor) }}</div>
              @if (detailKeys(item).length > 0) {
                <dl class="timeline-details">
                  @for (key of detailKeys(item); track key) {
                    <dt>{{ detailLabel(key) }}</dt>
                    <dd>{{ detailValue(item.details[key]) }}</dd>
                  }
                </dl>
              }
            </li>
          }
        </ol>

        @if (error() !== null) {
          <p role="alert">{{ errorMessage() }}</p>
        }

        @if (hasMore()) {
          <button type="button" [disabled]="loadingMore()" (click)="loadMore()">
            {{ loadingMore() ? 'Loading…' : error() !== null ? 'Retry' : 'Load more' }}
          </button>
        }
      }

      <p class="visually-hidden" aria-live="polite">{{ liveMessage() }}</p>
    </section>
  `,
})
export class WorkOrderTimelineComponent {
  readonly workOrderId = input.required<string>();

  /** Changing this (the Work Order's `rowVersion`) reloads the timeline from the first page. */
  readonly refreshKey = input<string | null>(null);

  private readonly workOrders = inject(WorkOrderService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly items = signal<AuditTimelineItem[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly hasMore = signal(false);
  protected readonly initialLoading = signal(true);
  protected readonly loadingMore = signal(false);
  protected readonly error = signal<TimelineError | null>(null);
  protected readonly liveMessage = signal('');

  private request: Subscription | null = null;
  private generation = 0;

  constructor() {
    effect(() => {
      this.workOrderId();
      this.refreshKey();
      untracked(() => this.reload());
    });
  }

  /** (Re)loads the first page; any request still in flight is cancelled and its response ignored. */
  protected reload(): void {
    this.request?.unsubscribe();
    const generation = ++this.generation;
    this.error.set(null);
    this.loadingMore.set(false);
    this.initialLoading.set(true);

    this.request = this.workOrders.getTimeline(this.workOrderId()).subscribe({
      next: (page) => {
        if (generation !== this.generation) {
          return;
        }

        this.items.set(dedupe(page.items));
        this.nextCursor.set(page.nextCursor);
        this.hasMore.set(page.hasMore);
        this.initialLoading.set(false);
        this.liveMessage.set(`${this.items().length} timeline events loaded.`);
      },
      error: (response: HttpErrorResponse) => {
        if (generation !== this.generation) {
          return;
        }

        this.items.set([]);
        this.nextCursor.set(null);
        this.hasMore.set(false);
        this.initialLoading.set(false);
        this.error.set(classify(response));
      },
    });
  }

  protected loadMore(): void {
    const cursor = this.nextCursor();
    if (this.loadingMore() || this.initialLoading() || cursor === null) {
      return;
    }

    const generation = this.generation;
    this.error.set(null);
    this.loadingMore.set(true);

    this.request = this.workOrders.getTimeline(this.workOrderId(), cursor).subscribe({
      next: (page) => {
        if (generation !== this.generation) {
          return;
        }

        const known = new Set(this.items().map((item) => item.auditId));
        const fresh = dedupe(page.items).filter((item) => !known.has(item.auditId));
        const firstNewIndex = this.items().length;

        this.items.update((current) => [...current, ...fresh]);
        this.nextCursor.set(page.nextCursor);
        this.hasMore.set(page.hasMore);
        this.loadingMore.set(false);
        this.liveMessage.set(`${fresh.length} more timeline events loaded; ${this.items().length} in total.`);

        if (fresh.length > 0) {
          afterNextRender(
            () => (this.host.nativeElement.querySelectorAll('li.timeline-item')[firstNewIndex] as HTMLElement | undefined)?.focus(),
            { injector: this.injector },
          );
        }
      },
      error: (response: HttpErrorResponse) => {
        if (generation !== this.generation) {
          return;
        }

        this.loadingMore.set(false);
        const kind = classify(response);
        if (kind === 'badCursor') {
          // The cursor is no longer accepted (e.g. the signing key rotated): restart from the first page.
          this.reload();
          return;
        }

        this.error.set(kind);
      },
    });
  }

  protected errorMessage(): string {
    switch (this.error()) {
      case 'forbidden':
        return 'You are not signed in, or your role cannot view this timeline.';
      case 'notFound':
        return 'This timeline does not exist, or is outside your data scope.';
      case 'badCursor':
        return 'The timeline changed. Retry to reload it from the start.';
      case 'failed':
        return 'Could not load the timeline. Retry.';
      default:
        return '';
    }
  }

  protected label(actionCode: string): string {
    return timelineActionLabel(actionCode);
  }

  /** Browser-local rendering of a UTC instant; the exact UTC value stays in `datetime` and `title`. */
  protected local(occurredAt: string): string {
    const date = new Date(occurredAt);
    return Number.isNaN(date.getTime()) ? occurredAt : date.toLocaleString();
  }

  protected actorText(actor: AuditTimelineActor): string {
    return actorLabel(actor.kind, actor.actorId);
  }

  protected detailKeys(item: AuditTimelineItem): string[] {
    return Object.keys(item.details ?? {});
  }

  protected detailLabel(key: string): string {
    return timelineDetailLabel(key);
  }

  protected detailValue(value: AuditTimelineDetailValue | undefined): string {
    return value === null || value === undefined ? '—' : String(value);
  }
}

function actorLabel(kind: AuditActorKind, actorId: string): string {
  switch (kind) {
    case 'SYSTEM':
      return 'System';
    case 'USER':
      return `user ${actorId.slice(0, 8)}`;
    default:
      return 'an unknown user';
  }
}

function classify(response: HttpErrorResponse): TimelineError {
  if (response.status === 401 || response.status === 403) {
    return 'forbidden';
  }

  if (response.status === 404) {
    return 'notFound';
  }

  return response.status === 400 ? 'badCursor' : 'failed';
}

/** Keeps the first occurrence of every `auditId` (a second layer next to the server's keyset guarantee). */
function dedupe(items: AuditTimelineItem[]): AuditTimelineItem[] {
  const seen = new Set<string>();
  return items.filter((item) => (seen.has(item.auditId) ? false : (seen.add(item.auditId), true)));
}
