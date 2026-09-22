import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal, viewChild } from '@angular/core';
import { ActiveWorkSessionComponent } from './active-work-session.component';
import { MyVisitSummary } from './my-visits.models';
import { MyVisitsService } from './my-visits.service';

const PAGE_SIZE = 20;

/**
 * "My Visits" (S3-001; UI-040): a Technician's own assigned, SCHEDULED Service Visits, with a Check-in button
 * per row. The list is already scoped and filtered server-side (`GET /service-visits/mine` returns only the
 * caller's own SCHEDULED visits) — no client-side re-filtering. A successful Check-in reloads the list, since
 * the checked-in visit is no longer SCHEDULED and drops out of it.
 */
@Component({
  selector: 'app-my-visits',
  standalone: true,
  imports: [ActiveWorkSessionComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>My Visits</h1>

    <app-active-work-session />

    @if (loading()) {
      <p>Loading…</p>
    } @else if (unauthorized()) {
      <p role="alert">You are not signed in, or your role cannot view this page. Sign-in is not part of this feature yet.</p>
    } @else if (error()) {
      <p role="alert">Could not load My Visits: {{ error() }}</p>
    } @else if (items().length === 0) {
      <p>No assigned visits to check in to.</p>
    } @else {
      <table>
        <thead>
          <tr>
            <th>WO No.</th>
            <th>Site Code</th>
            <th>Equipment Code</th>
            <th>Scheduled</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (visit of items(); track visit.serviceVisitId) {
            <tr>
              <td>{{ visit.workOrderNo }}</td>
              <td>{{ visit.siteCode ?? '—' }}</td>
              <td>{{ visit.equipmentCode ?? '—' }}</td>
              <td>{{ visit.scheduledStartAt ?? '—' }} to {{ visit.scheduledEndAt ?? '—' }}</td>
              <td>
                <button type="button" [disabled]="checkingIn() === visit.serviceVisitId" (click)="onCheckIn(visit)">
                  Check-in
                </button>
              </td>
            </tr>
          }
        </tbody>
      </table>

      <div>
        <button type="button" [disabled]="page() <= 1" (click)="goToPage(page() - 1)">Previous</button>
        <span>Page {{ page() }} of {{ totalPages() }} ({{ totalCount() }} total)</span>
        <button type="button" [disabled]="page() >= totalPages()" (click)="goToPage(page() + 1)">Next</button>
      </div>
    }

    @if (actionError()) {
      <p role="alert">{{ actionError() }}</p>
    }
  `,
})
export class MyVisitsComponent {
  private readonly myVisits = inject(MyVisitsService);
  private readonly activeSession = viewChild(ActiveWorkSessionComponent);

  protected readonly items = signal<MyVisitSummary[]>([]);
  protected readonly page = signal(1);
  protected readonly totalCount = signal(0);
  protected readonly loading = signal(true);
  protected readonly unauthorized = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly checkingIn = signal<string | null>(null);
  protected readonly actionError = signal<string | null>(null);

  protected readonly totalPages = () => Math.max(1, Math.ceil(this.totalCount() / PAGE_SIZE));

  constructor() {
    this.load();
  }

  protected goToPage(page: number): void {
    this.load(page);
  }

  protected onCheckIn(visit: MyVisitSummary): void {
    this.checkingIn.set(visit.serviceVisitId);
    this.actionError.set(null);

    this.myVisits.checkIn(visit.serviceVisitId, `"${visit.rowVersion}"`).subscribe({
      next: () => {
        this.checkingIn.set(null);
        this.load();
        // The new session is not in the visits list; the Active Work Session panel picks it up.
        this.activeSession()?.refresh();
      },
      error: (response: HttpErrorResponse) => {
        this.checkingIn.set(null);
        this.actionError.set(this.describe(response));
      },
    });
  }

  private load(page = this.page()): void {
    this.loading.set(true);
    this.unauthorized.set(false);
    this.error.set(null);

    this.myVisits.list(page, PAGE_SIZE).subscribe({
      next: (response) => {
        this.items.set(response.items);
        this.page.set(response.page);
        this.totalCount.set(response.totalCount);
        this.loading.set(false);
      },
      error: (response: HttpErrorResponse) => {
        this.loading.set(false);
        if (response.status === 401 || response.status === 403) {
          this.unauthorized.set(true);
        } else {
          this.error.set(this.fallbackMessage(response));
        }
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
        return 'This visit was not found, or it is no longer assigned to you. Reload to see your current visits.';
      case 409:
        // STATE_CONFLICT carries an understandable server message (e.g. an active Work Session already exists
        // on another visit); a stale-token CONCURRENCY_CONFLICT keeps the generic reload hint.
        return problem?.code === 'STATE_CONFLICT' && problem.detail
          ? problem.detail
          : 'This record was changed or is no longer in a valid state. Reload to see its current state.';
      case 422:
        return problem?.errors ? Object.values(problem.errors).flat().join(' ') : 'One or more fields are invalid.';
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
