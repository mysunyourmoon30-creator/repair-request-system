import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { WORK_ORDER_STATUSES, WorkOrder } from './work-order.models';
import { WorkOrderService } from './work-order.service';

const PAGE_SIZE = 20;

/**
 * S2-001 Work Order List: WO No., Repair Request No., Customer Code, Site Code, Equipment Code and Status,
 * with a status filter, paging and a fixed newest-first sort (DEC-S2-001-04, `docs/12` Section 11).
 * Scheduled Date and Assigned Team/Technician are not shown (DEC-S2-001-02/03).
 */
@Component({
  selector: 'app-work-order-list',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1>Work Orders</h1>

    <label>
      Status
      <select [value]="statusFilter()" (change)="onStatusChange($event)">
        <option value="">All statuses</option>
        @for (status of statuses; track status) {
          <option [value]="status">{{ status }}</option>
        }
      </select>
    </label>

    @if (loading()) {
      <p>Loading…</p>
    } @else if (unauthorized()) {
      <p role="alert">
        You are not signed in, or your role cannot view Work Orders. Sign-in is not part of this feature yet.
      </p>
    } @else if (error()) {
      <p role="alert">Could not load Work Orders: {{ error() }}</p>
    } @else if (items().length === 0) {
      <p>No Work Orders found.</p>
    } @else {
      <table>
        <thead>
          <tr>
            <th>WO No.</th>
            <th>Repair Request No.</th>
            <th>Customer Code</th>
            <th>Site Code</th>
            <th>Equipment Code</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          @for (workOrder of items(); track workOrder.workOrderId) {
            <tr>
              <td><a [routerLink]="['/work-orders', workOrder.workOrderId]">{{ workOrder.workOrderNo }}</a></td>
              <td>{{ workOrder.repairRequestNo ?? '—' }}</td>
              <td>{{ workOrder.customerCode ?? '—' }}</td>
              <td>{{ workOrder.siteCode ?? '—' }}</td>
              <td>{{ workOrder.equipmentCode ?? '—' }}</td>
              <td>{{ workOrder.status }}</td>
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
  `,
})
export class WorkOrderListComponent {
  private readonly workOrders = inject(WorkOrderService);

  protected readonly statuses = WORK_ORDER_STATUSES;

  protected readonly items = signal<WorkOrder[]>([]);
  protected readonly page = signal(1);
  protected readonly totalCount = signal(0);
  protected readonly statusFilter = signal('');
  protected readonly loading = signal(true);
  protected readonly unauthorized = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly totalPages = () => Math.max(1, Math.ceil(this.totalCount() / PAGE_SIZE));

  constructor() {
    this.load();
  }

  protected onStatusChange(event: Event): void {
    this.statusFilter.set((event.target as HTMLSelectElement).value);
    this.load(1);
  }

  protected goToPage(page: number): void {
    this.load(page);
  }

  private load(page = this.page()): void {
    this.loading.set(true);
    this.unauthorized.set(false);
    this.error.set(null);

    this.workOrders.list(page, PAGE_SIZE, this.statusFilter() || null).subscribe({
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
          this.error.set(response.message);
        }
      },
    });
  }
}
