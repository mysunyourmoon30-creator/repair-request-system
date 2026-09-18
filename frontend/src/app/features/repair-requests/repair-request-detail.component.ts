import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { RepairRequest } from './repair-request.models';
import { RepairRequestService } from './repair-request.service';

/**
 * S2-002 Repair Request Detail: a minimal read-only view whose sole action is Convert (RR-API-010,
 * ST-RR-008), shown only while the request is APPROVED. On success the caller is sent straight to the
 * newly created Work Order (the response's `workOrderId`).
 */
@Component({
  selector: 'app-repair-request-detail',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p><a routerLink="/repair-requests">&larr; Back to Repair Requests</a></p>

    @if (loading()) {
      <p>Loading…</p>
    } @else if (unauthorized()) {
      <p role="alert">
        You are not signed in, or your role cannot view this Repair Request. Sign-in is not part of this feature yet.
      </p>
    } @else if (notFound()) {
      <p role="alert">This Repair Request does not exist, or is outside your data scope.</p>
    } @else if (error()) {
      <p role="alert">Could not load this Repair Request: {{ error() }}</p>
    } @else if (repairRequest(); as repairRequest) {
      <h1>Repair Request {{ repairRequest.requestNo ?? repairRequest.id }}</h1>
      <dl>
        <dt>Status</dt>
        <dd>{{ repairRequest.status }}</dd>
        <dt>Description</dt>
        <dd>{{ repairRequest.description ?? '—' }}</dd>
      </dl>

      @if (repairRequest.status === 'APPROVED') {
        <button type="button" [disabled]="converting()" (click)="convert()">Convert to Work Order</button>
      }

      @if (convertError()) {
        <p role="alert">{{ convertError() }}</p>
      }
    }
  `,
})
export class RepairRequestDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly repairRequests = inject(RepairRequestService);

  protected readonly repairRequest = signal<RepairRequest | null>(null);
  protected readonly loading = signal(true);
  protected readonly unauthorized = signal(false);
  protected readonly notFound = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly converting = signal(false);
  protected readonly convertError = signal<string | null>(null);

  private repairRequestId: string | null = null;
  private eTag: string | null = null;

  constructor() {
    this.repairRequestId = this.route.snapshot.paramMap.get('id');
    if (!this.repairRequestId) {
      this.loading.set(false);
      this.notFound.set(true);
      return;
    }

    this.load(this.repairRequestId);
  }

  private load(repairRequestId: string): void {
    this.repairRequests.get(repairRequestId).subscribe({
      next: ({ repairRequest, eTag }) => {
        this.repairRequest.set(repairRequest);
        this.eTag = eTag;
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

  protected convert(): void {
    if (!this.repairRequestId || !this.eTag || this.converting()) {
      return;
    }

    this.converting.set(true);
    this.convertError.set(null);
    this.repairRequests.convert(this.repairRequestId, this.eTag).subscribe({
      next: (workOrder) => {
        this.router.navigate(['/work-orders', workOrder.workOrderId]);
      },
      error: (response: HttpErrorResponse) => {
        this.converting.set(false);
        if (response.status === 403) {
          this.convertError.set('You do not have permission to convert this Repair Request.');
        } else if (response.status === 409) {
          this.convertError.set(
            'This Repair Request was changed or already converted elsewhere. Reload to see its current state.',
          );
        } else {
          this.convertError.set(response.message);
        }
      },
    });
  }
}
