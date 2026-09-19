import { Routes } from '@angular/router';
import { technicianOnlyGuard } from './core/auth/technician-only.guard';

export const routes: Routes = [
  {
    path: 'work-orders',
    loadComponent: () =>
      import('./features/work-orders/work-order-list.component').then((m) => m.WorkOrderListComponent),
  },
  {
    path: 'work-orders/:id',
    loadComponent: () =>
      import('./features/work-orders/work-order-detail.component').then((m) => m.WorkOrderDetailComponent),
  },
  {
    path: 'my-visits',
    canActivate: [technicianOnlyGuard],
    loadComponent: () => import('./features/technician/my-visits.component').then((m) => m.MyVisitsComponent),
  },
  {
    path: 'repair-requests',
    loadComponent: () =>
      import('./features/repair-requests/repair-request-list.component').then((m) => m.RepairRequestListComponent),
  },
  {
    path: 'repair-requests/:id',
    loadComponent: () =>
      import('./features/repair-requests/repair-request-detail.component').then(
        (m) => m.RepairRequestDetailComponent,
      ),
  },
];
