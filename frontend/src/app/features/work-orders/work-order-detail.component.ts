import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { MISSED_VISIT_DECISIONS, ServiceVisit, WorkOrder } from './work-order.models';
import { WorkOrderService } from './work-order.service';

/**
 * Work Order Detail: the S2-001 read-only view, extended in S2-003 with Schedule (shown while OPEN) and, per
 * Service Visit, its status-gated actions (Reschedule/Reassign/Cancel/Mark Missed while SCHEDULED, Decide Missed
 * while MISSED). Team/technician fields are plain text ids — there is no directory to pick a display name from
 * (no Team master data exists, and technician eligibility is checked server-side). `datetime-local` inputs are
 * treated as UTC directly (no timezone picker in this minimal form).
 */
@Component({
  selector: 'app-work-order-detail',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p><a routerLink="/work-orders">&larr; Back to Work Orders</a></p>

    @if (loading()) {
      <p>Loading…</p>
    } @else if (unauthorized()) {
      <p role="alert">
        You are not signed in, or your role cannot view this Work Order. Sign-in is not part of this feature yet.
      </p>
    } @else if (notFound()) {
      <p role="alert">This Work Order does not exist, or is outside your data scope.</p>
    } @else if (error()) {
      <p role="alert">Could not load this Work Order: {{ error() }}</p>
    } @else if (workOrder(); as workOrder) {
      <h1>Work Order {{ workOrder.workOrderNo }}</h1>
      <dl>
        <dt>Status</dt>
        <dd>{{ workOrder.status }}</dd>
        <dt>Repair Request No.</dt>
        <dd>{{ workOrder.repairRequestNo ?? '—' }}</dd>
        <dt>Customer Code</dt>
        <dd>{{ workOrder.customerCode ?? '—' }}</dd>
        <dt>Site Code</dt>
        <dd>{{ workOrder.siteCode ?? '—' }}</dd>
        <dt>Equipment Code</dt>
        <dd>{{ workOrder.equipmentCode ?? '—' }}</dd>
      </dl>

      @if (workOrder.status === 'OPEN') {
        <h2>Schedule</h2>
        <form (submit)="onSchedule($event, workOrder, teamId, technicianId, startAt, endAt)">
          <label>Team ID <input #teamId type="text" required /></label>
          <label>Technician ID <input #technicianId type="text" required /></label>
          <label>Scheduled Start <input #startAt type="datetime-local" required /></label>
          <label>Scheduled End <input #endAt type="datetime-local" required /></label>
          <button type="submit" [disabled]="submitting()">Schedule</button>
        </form>
      }

      @if (workOrder.visits.length > 0) {
        <h2>Service Visits</h2>
        @for (visit of workOrder.visits; track visit.serviceVisitId) {
          <article>
            <dl>
              <dt>Status</dt>
              <dd>{{ visit.status }}</dd>
              <dt>Type</dt>
              <dd>{{ visit.visitType }}</dd>
              <dt>Team ID</dt>
              <dd>{{ visit.assignedTeamId ?? '—' }}</dd>
              <dt>Technician ID</dt>
              <dd>{{ visit.assignedTechnicianId ?? '—' }}</dd>
              <dt>Scheduled</dt>
              <dd>{{ visit.scheduledStartAt ?? '—' }} to {{ visit.scheduledEndAt ?? '—' }}</dd>
              @if (visit.missedDecisionCode) {
                <dt>Missed Decision</dt>
                <dd>{{ visit.missedDecisionCode }}</dd>
              }
            </dl>

            @if (visit.status === 'SCHEDULED') {
              <details>
                <summary>Reschedule</summary>
                <form (submit)="onReschedule($event, visit, rReason, rStart, rEnd)">
                  <label>Reason <input #rReason type="text" required /></label>
                  <label>New Start <input #rStart type="datetime-local" required /></label>
                  <label>New End <input #rEnd type="datetime-local" required /></label>
                  <button type="submit" [disabled]="submitting()">Reschedule</button>
                </form>
              </details>

              <details>
                <summary>Reassign</summary>
                <form (submit)="onReassign($event, visit, aReason, aTeam, aTechnician)">
                  <label>Reason <input #aReason type="text" required /></label>
                  <label>New Team ID <input #aTeam type="text" required /></label>
                  <label>New Technician ID <input #aTechnician type="text" required /></label>
                  <button type="submit" [disabled]="submitting()">Reassign</button>
                </form>
              </details>

              <details>
                <summary>Cancel Visit</summary>
                <form (submit)="onCancelVisit($event, visit, cReason)">
                  <label>Reason <input #cReason type="text" required /></label>
                  <button type="submit" [disabled]="submitting()">Cancel Visit</button>
                </form>
              </details>

              <details>
                <summary>Mark Missed</summary>
                <form (submit)="onMarkMissed($event, visit, mReason)">
                  <label>Reason <input #mReason type="text" required /></label>
                  <button type="submit" [disabled]="submitting()">Mark Missed</button>
                </form>
              </details>
            }

            @if (visit.status === 'MISSED' && !visit.missedDecisionCode) {
              <details open>
                <summary>Decide Missed</summary>
                <form (submit)="onDecideMissed($event, visit, decision, dReason, nsTeam, nsTechnician, nsStart, nsEnd)">
                  <label>
                    Decision
                    <select #decision required>
                      @for (option of missedDecisions; track option) {
                        <option [value]="option">{{ option }}</option>
                      }
                    </select>
                  </label>
                  <label>Reason <input #dReason type="text" required /></label>
                  <p>New schedule (required unless NO_FOLLOW_UP):</p>
                  <label>New Team ID <input #nsTeam type="text" /></label>
                  <label>New Technician ID <input #nsTechnician type="text" /></label>
                  <label>New Start <input #nsStart type="datetime-local" /></label>
                  <label>New End <input #nsEnd type="datetime-local" /></label>
                  <button type="submit" [disabled]="submitting()">Submit Decision</button>
                </form>
              </details>
            }
          </article>
        }
      }

      @if (actionError()) {
        <p role="alert">{{ actionError() }}</p>
      }
    }
  `,
})
export class WorkOrderDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly workOrders = inject(WorkOrderService);

  protected readonly workOrder = signal<WorkOrder | null>(null);
  protected readonly loading = signal(true);
  protected readonly unauthorized = signal(false);
  protected readonly notFound = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly submitting = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected readonly missedDecisions = MISSED_VISIT_DECISIONS;

  constructor() {
    const workOrderId = this.route.snapshot.paramMap.get('id');
    if (!workOrderId) {
      this.loading.set(false);
      this.notFound.set(true);
      return;
    }

    this.workOrders.get(workOrderId).subscribe({
      next: (workOrder) => {
        this.workOrder.set(workOrder);
        this.loading.set(false);
      },
      error: (response: HttpErrorResponse) => {
        this.loading.set(false);
        if (response.status === 401 || response.status === 403) {
          this.unauthorized.set(true);
        } else if (response.status === 404) {
          this.notFound.set(true);
        } else {
          this.error.set(response.message);
        }
      },
    });
  }

  protected onSchedule(
    event: Event,
    workOrder: WorkOrder,
    teamId: HTMLInputElement,
    technicianId: HTMLInputElement,
    startAt: HTMLInputElement,
    endAt: HTMLInputElement,
  ): void {
    event.preventDefault();
    this.run(
      this.workOrders.schedule(workOrder.workOrderId, this.quoted(workOrder.rowVersion), {
        ownerTeamId: teamId.value,
        assignedTechnicianId: technicianId.value,
        scheduledStartAt: this.toIso(startAt.value),
        scheduledEndAt: this.toIso(endAt.value),
      }),
    );
  }

  protected onReschedule(event: Event, visit: ServiceVisit, reason: HTMLInputElement, start: HTMLInputElement, end: HTMLInputElement): void {
    event.preventDefault();
    this.run(
      this.workOrders.reschedule(visit.serviceVisitId, this.quoted(visit.rowVersion), {
        reason: reason.value,
        scheduledStartAt: this.toIso(start.value),
        scheduledEndAt: this.toIso(end.value),
      }),
    );
  }

  protected onReassign(event: Event, visit: ServiceVisit, reason: HTMLInputElement, teamId: HTMLInputElement, technicianId: HTMLInputElement): void {
    event.preventDefault();
    this.run(
      this.workOrders.reassign(visit.serviceVisitId, this.quoted(visit.rowVersion), {
        reason: reason.value,
        assignedTeamId: teamId.value,
        assignedTechnicianId: technicianId.value,
      }),
    );
  }

  protected onCancelVisit(event: Event, visit: ServiceVisit, reason: HTMLInputElement): void {
    event.preventDefault();
    this.run(this.workOrders.cancelVisit(visit.serviceVisitId, this.quoted(visit.rowVersion), { reason: reason.value }));
  }

  protected onMarkMissed(event: Event, visit: ServiceVisit, reason: HTMLInputElement): void {
    event.preventDefault();
    this.run(this.workOrders.markMissed(visit.serviceVisitId, this.quoted(visit.rowVersion), { reason: reason.value }));
  }

  protected onDecideMissed(
    event: Event,
    visit: ServiceVisit,
    decision: HTMLSelectElement,
    reason: HTMLInputElement,
    newTeamId: HTMLInputElement,
    newTechnicianId: HTMLInputElement,
    newStart: HTMLInputElement,
    newEnd: HTMLInputElement,
  ): void {
    event.preventDefault();
    const hasNewSchedule = newTeamId.value || newTechnicianId.value || newStart.value || newEnd.value;
    this.run(
      this.workOrders.decideMissed(visit.serviceVisitId, this.quoted(visit.rowVersion), {
        decision: decision.value,
        reason: reason.value,
        newSchedule: hasNewSchedule
          ? {
              assignedTeamId: newTeamId.value,
              assignedTechnicianId: newTechnicianId.value,
              scheduledStartAt: this.toIso(newStart.value),
              scheduledEndAt: this.toIso(newEnd.value),
            }
          : null,
      }),
    );
  }

  private run(request: Observable<WorkOrder>): void {
    this.submitting.set(true);
    this.actionError.set(null);

    request.subscribe({
      next: (workOrder) => {
        this.workOrder.set(workOrder);
        this.submitting.set(false);
      },
      error: (response: HttpErrorResponse) => {
        this.submitting.set(false);
        this.actionError.set(this.describe(response));
      },
    });
  }

  private describe(response: HttpErrorResponse): string {
    if (response.status === 403) {
      return 'You do not have permission to perform this action.';
    }

    if (response.status === 409) {
      return 'This record was changed or is no longer in a valid state. Reload to see its current state.';
    }

    if (response.status === 422) {
      const errors = (response.error as { errors?: Record<string, string[]> } | null)?.errors;
      if (errors) {
        return Object.values(errors).flat().join(' ');
      }

      return 'One or more fields are invalid.';
    }

    return response.message;
  }

  /** `datetime-local` carries no timezone; treated as UTC directly for this minimal form. */
  private toIso(value: string): string {
    return `${value}Z`;
  }

  private quoted(rowVersion: string): string {
    return `"${rowVersion}"`;
  }
}
