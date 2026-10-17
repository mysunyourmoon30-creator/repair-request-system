import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import {
  ServiceReportCost,
  ServiceReportEvidence,
  ServiceReportResponse,
  ServiceReportSummary,
  ServiceReportVisit,
  hasCostSection,
} from './service-report.models';
import { getCurrentUserRoles } from '../../core/auth/current-user-role';
import { TimeCorrectionRequestComponent } from '../time-corrections/time-correction-request.component';
import { ServiceReportService } from './service-report.service';
import { EvidenceType } from './work-evidence.models';

type ReportError = 'unauthorized' | 'forbidden' | 'notFound' | 'failed';

const EVIDENCE_TYPE_LABELS: Record<EvidenceType, string> = {
  BEFORE_WORK: 'Before work',
  AFTER_WORK: 'After work',
  OTHER: 'Other',
};

/**
 * Service Report (SVR-API-001; `docs/13` §4.28; `docs/16` D1, OD4/OD5): the read-only operational completion record of one Work
 * Order — header, Service Visits with their Work Sessions, Work Summary rounds with their frozen CLEAN evidence, Customer Acceptance
 * and, only for an in-scope Team Lead or Supervisor, the Cost Summary.
 *
 * - The Cost section is rendered if and only if the server's response contains the `costSummary` property
 *   ({@link hasCostSection}); nothing about cost is derived from the user's role, requested separately or stored. For the Technician and
 *   the Acceptance Contact the property does not exist in the response at all.
 * - Evidence content is opened only through the authorized download (WEV-API-003) as a Blob; the server decides CLEAN status, binding and
 *   audience on every click. A file name shown or used for the saved file is the server's sanitized display name.
 * - States: loading, 401 / 403 / 404 / failure (with Retry); a duplicate load or download request is ignored while one is in flight.
 * - No `localStorage`/`sessionStorage`. UX only: the backend re-checks role, scope and the exact-contact rule on every request.
 */
@Component({
  selector: 'app-service-report',
  standalone: true,
  imports: [RouterLink, TimeCorrectionRequestComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: `
    .report-section {
      margin: 1.25rem 0;
    }
    .report-list {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .report-list > li {
      padding: 0.5rem 0;
      border-bottom: 1px solid #ddd;
    }
    .report-facts {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 0 0.75rem;
      margin: 0;
    }
    .report-facts dd {
      margin: 0;
      overflow-wrap: anywhere;
    }
    .summary-text {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }
    @media (max-width: 40rem) {
      .report-facts {
        grid-template-columns: 1fr;
      }
    }
  `,
  template: `
    <p><a routerLink="/work-orders">&larr; Back to Work Orders</a></p>

    @if (loading()) {
      <p role="status">Loading the Service Report…</p>
    } @else if (error() === 'unauthorized') {
      <p role="alert">You are not signed in. Sign-in is not part of this feature yet.</p>
    } @else if (error() === 'forbidden') {
      <p role="alert">Your role cannot view the Service Report.</p>
    } @else if (error() === 'notFound') {
      <p role="alert">This Service Report does not exist, or you do not have access to it.</p>
    } @else if (error() === 'failed') {
      <p role="alert">The Service Report could not be loaded.</p>
      <button type="button" [disabled]="loading()" (click)="load()">Retry</button>
    } @else if (report(); as report) {
      <h1>Service Report {{ report.workOrder.workOrderNo }}</h1>

      <section class="report-section" aria-labelledby="sr-work-order">
        <h2 id="sr-work-order">Work Order</h2>
        <dl class="report-facts">
          <dt>Status</dt>
          <dd>{{ report.workOrder.status }}</dd>
          <dt>Repair Request No.</dt>
          <dd>{{ report.workOrder.repairRequestNo ?? '—' }}</dd>
          <dt>Customer Code</dt>
          <dd>{{ report.workOrder.customerCode ?? '—' }}</dd>
          <dt>Site Code</dt>
          <dd>{{ report.workOrder.siteCode ?? '—' }}</dd>
          <dt>Equipment Code</dt>
          <dd>{{ report.workOrder.equipmentCode ?? '—' }}</dd>
          <dt>Created (UTC)</dt>
          <dd>{{ report.workOrder.createdAt }}</dd>
          @if (report.workOrder.closedAt !== null) {
            <dt>Closed (UTC)</dt>
            <dd>{{ report.workOrder.closedAt }}</dd>
          }
        </dl>
      </section>

      @if (canRequestTimeCorrection()) {
        <section class="report-section" aria-labelledby="sr-time-correction">
          <h2 id="sr-time-correction">Time corrections</h2>
          <button type="button" [attr.aria-expanded]="timeCorrectionOpen()" (click)="timeCorrectionOpen.set(!timeCorrectionOpen())">
            {{ timeCorrectionOpen() ? 'Hide time correction request' : 'Request time correction' }}
          </button>
          @if (timeCorrectionOpen()) {
            <app-time-correction-request [workOrderId]="report.workOrder.workOrderId" />
          }
        </section>
      }

      <section class="report-section" aria-labelledby="sr-visits">
        <h2 id="sr-visits">Service Visits</h2>
        @if (report.visits.length === 0) {
          <p>No Service Visit has been recorded.</p>
        } @else {
          <ul class="report-list">
            @for (visit of report.visits; track visit.serviceVisitId) {
              <li>
                <strong>{{ visit.visitType }}</strong> — {{ visit.status }}
                <dl class="report-facts">
                  <dt>Scheduled (UTC)</dt>
                  <dd>{{ visit.scheduledStartAt ?? '—' }} to {{ visit.scheduledEndAt ?? '—' }}</dd>
                  <dt>Completed (UTC)</dt>
                  <dd>{{ visit.completedAt ?? '—' }}</dd>
                </dl>
                @for (session of visit.sessions; track session.workSessionId) {
                  <p>
                    Work Session {{ session.status }}: checked in {{ session.checkInAt }}, checked out {{ session.checkOutAt ?? '—' }}
                  </p>
                  @if (session.pauses.length > 0) {
                    <ul aria-label="Pauses of this Work Session">
                      @for (pause of session.pauses; track pause.pausedAt) {
                        <li>Paused {{ pause.pausedAt }}, resumed {{ pause.resumedAt ?? '—' }}</li>
                      }
                    </ul>
                  }
                }
              </li>
            }
          </ul>
        }
      </section>

      <section class="report-section" aria-labelledby="sr-summary">
        <h2 id="sr-summary">Work Summary and evidence</h2>
        @if (report.workSummaries.length === 0) {
          <p>No Work Summary has been submitted yet.</p>
        } @else {
          <ul class="report-list">
            @for (summary of report.workSummaries; track summary.workSummaryId) {
              <li>
                <h3>{{ visitLabel(report.visits, summary) }}</h3>
                <p class="summary-text">{{ summary.summaryText }}</p>
                <p>Outcome: <strong>{{ summary.repairOutcomeCode }}</strong></p>
                @if (summary.evidence.length === 0) {
                  <p>No evidence is bound to this Work Summary.</p>
                } @else {
                  <ul [attr.aria-label]="'Evidence of ' + visitLabel(report.visits, summary)">
                    @for (item of summary.evidence; track item.evidenceId) {
                      <li>
                        {{ typeLabel(item.evidenceType) }} — {{ item.fileName }} ({{ sizeLabel(item.sizeBytes) }})
                        <button
                          type="button"
                          [disabled]="downloading() !== null"
                          [attr.aria-label]="'Download ' + item.fileName"
                          (click)="download(item)"
                        >
                          {{ downloading() === item.evidenceId ? 'Downloading…' : 'Download' }}
                        </button>
                      </li>
                    }
                  </ul>
                }
              </li>
            }
          </ul>
        }
        @if (downloadError(); as message) {
          <p role="alert">{{ message }}</p>
        }
      </section>

      <section class="report-section" aria-labelledby="sr-acceptance">
        <h2 id="sr-acceptance">Customer Acceptance</h2>
        @if (report.acceptance.length === 0) {
          <p>No customer decision has been recorded yet.</p>
        } @else {
          <ol class="report-list">
            @for (round of report.acceptance; track round.roundNo) {
              <li>
                Round {{ round.roundNo }}: <strong>{{ round.decision }}</strong> ({{ round.decidedAt }})
                @if (round.decisionReason) {
                  <span> — {{ round.decisionReason }}</span>
                }
              </li>
            }
          </ol>
        }
      </section>

      @if (cost(); as cost) {
        <section class="report-section" aria-labelledby="sr-cost">
          <h2 id="sr-cost">Cost Summary</h2>
          <dl class="report-facts">
            <dt>Total</dt>
            <dd>{{ cost.totalAmount }} {{ cost.currencyCode }}</dd>
            <dt>Note</dt>
            <dd>{{ cost.note ?? '—' }}</dd>
            <dt>Prepared (UTC)</dt>
            <dd>{{ cost.preparedAt }}</dd>
            <dt>Reviewed (UTC)</dt>
            <dd>{{ cost.reviewedAt ?? 'Not reviewed yet' }}</dd>
          </dl>
        </section>
      } @else if (costSectionPresent()) {
        <section class="report-section" aria-labelledby="sr-cost">
          <h2 id="sr-cost">Cost Summary</h2>
          <p>No Cost Summary has been prepared yet.</p>
        </section>
      }
    }
  `,
})
export class ServiceReportComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly reports = inject(ServiceReportService);

  /**
   * Technician and Team Lead may request a time correction for sessions already on this report, completed ones included (`docs/17` row 17).
   * Convenience only: TIME-API-ADD-002 returns only the caller's own (Technician) or Site-scoped (Team Lead) sessions and TIME-API-001 re-checks.
   */
  protected readonly timeCorrectionOpen = signal(false);
  protected canRequestTimeCorrection(): boolean {
    const roles = getCurrentUserRoles();
    return roles.includes('TECHNICIAN') || roles.includes('TEAM_LEAD');
  }

  protected readonly report = signal<ServiceReportResponse | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<ReportError | null>(null);
  protected readonly downloading = signal<string | null>(null);
  protected readonly downloadError = signal<string | null>(null);

  /** The Cost section exists only when the server sent it. */
  protected readonly costSectionPresent = computed(() => {
    const current = this.report();
    return current !== null && hasCostSection(current);
  });

  protected readonly cost = computed<ServiceReportCost | null>(() => {
    const current = this.report();
    return current !== null && hasCostSection(current) ? current.costSummary : null;
  });

  private workOrderId = '';
  private request: Subscription | null = null;
  private downloadRequest: Subscription | null = null;

  constructor() {
    this.workOrderId = this.route.snapshot.paramMap.get('id') ?? '';
    this.load();
  }

  /** (Re)loads the report; a request still in flight is cancelled so a stale response can never replace a newer one. */
  protected load(): void {
    if (!this.workOrderId) {
      this.loading.set(false);
      this.error.set('notFound');
      return;
    }

    this.request?.unsubscribe();
    this.error.set(null);
    this.loading.set(true);

    this.request = this.reports.getServiceReport(this.workOrderId).subscribe({
      next: (response) => {
        this.report.set(response);
        this.loading.set(false);
      },
      error: (response: HttpErrorResponse) => {
        this.report.set(null);
        this.loading.set(false);
        this.error.set(classify(response));
      },
    });
  }

  protected typeLabel(type: EvidenceType): string {
    return EVIDENCE_TYPE_LABELS[type] ?? type;
  }

  protected sizeLabel(bytes: number): string {
    if (bytes >= 1024 * 1024) {
      return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
    }

    return bytes >= 1024 ? `${Math.round(bytes / 1024)} KB` : `${bytes} B`;
  }

  protected visitLabel(visits: ServiceVisitLike[], summary: ServiceReportSummary): string {
    const visit = visits.find((item) => item.serviceVisitId === summary.serviceVisitId);
    return visit ? `${visit.visitType} visit` : 'Work Summary';
  }

  /** Authorized CLEAN-only download; one at a time, so a double click never issues two requests. */
  protected download(item: ServiceReportEvidence): void {
    if (this.downloading() !== null) {
      return;
    }

    this.downloadError.set(null);
    this.downloading.set(item.evidenceId);

    this.downloadRequest?.unsubscribe();
    this.downloadRequest = this.reports.downloadEvidence(this.workOrderId, item.evidenceId).subscribe({
      next: (blob) => {
        saveBlob(blob, item.fileName);
        this.downloading.set(null);
      },
      error: (response: HttpErrorResponse) => {
        this.downloading.set(null);
        this.downloadError.set(
          response.status === 409 || response.status === 404 || response.status === 403
            ? 'This evidence file is not available for download.'
            : 'The evidence file could not be downloaded. Try again.',
        );
      },
    });
  }
}

type ServiceVisitLike = Pick<ServiceReportVisit, 'serviceVisitId' | 'visitType'>;

function classify(response: HttpErrorResponse): ReportError {
  switch (response.status) {
    case 401:
      return 'unauthorized';
    case 403:
      return 'forbidden';
    case 404:
      return 'notFound';
    default:
      return 'failed';
  }
}

/** Saves the Blob under the server's sanitized display name; the temporary object URL is always released. */
function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.rel = 'noopener';
    anchor.click();
  } finally {
    URL.revokeObjectURL(url);
  }
}
