import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { WorkOrder } from './work-order.models';
import { WorkOrderService } from './work-order.service';

/**
 * S2-001 Work Order Detail: the minimal read-only view reachable from the list (route + fields already
 * carried by `WorkOrderResponse`). Scheduled Date and Assigned Team/Technician are not shown
 * (DEC-S2-001-02/03, `docs/12` Section 11).
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
}
