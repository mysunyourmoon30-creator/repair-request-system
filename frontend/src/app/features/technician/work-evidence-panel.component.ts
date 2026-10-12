import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import {
  EVIDENCE_ACCEPT,
  EVIDENCE_MAX_BYTES,
  EVIDENCE_TYPES,
  EvidenceType,
  WorkEvidenceItem,
} from '../work-orders/work-evidence.models';
import { WorkEvidenceService } from '../work-orders/work-evidence.service';

/**
 * Evidence of the Technician's next Work Summary (WEV-API-001/002; `docs/13` §4.28; `docs/16` OD1/OD2), shown on demand inside the active
 * Work Session panel. One file at a time with a closed evidence type; the item starts PENDING and only counts for "Submit Work Summary" once
 * the malware scan has marked it CLEAN — "Refresh status" re-reads the list. PENDING and FAILED items are shown as such and never offered
 * as usable. Duplicate uploads are prevented while a request is in flight. Role, ownership, the IN_PROGRESS state and the file policy are
 * enforced by the backend; the checks here (type and size) are convenience only. The ten-item limit applies to the evidence selected at
 * Submit, not to uploads.
 */
@Component({
  selector: 'app-work-evidence-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section aria-labelledby="evidence-heading">
      <h3 id="evidence-heading">Evidence</h3>
      <p>
        Upload at least one photo or PDF (JPG, PNG or PDF, up to 10 MB each). Evidence can be used for the Work Summary only after the malware
        scan marks it CLEAN; use Refresh status to check. You choose which CLEAN items to submit when you submit the Work Summary.
      </p>

      <form (submit)="onUpload($event)">
        <label>
          Evidence type
          <select #typeSelect [value]="evidenceType()" (change)="evidenceType.set($any(typeSelect.value))">
            @for (option of evidenceTypes; track option.code) {
              <option [value]="option.code">{{ option.label }}</option>
            }
          </select>
        </label>
        <label>
          File
          <input #fileInput type="file" [attr.accept]="accept" (change)="onFileChosen(fileInput.files)" />
        </label>
        <button type="submit" [disabled]="!canUpload()">{{ uploading() ? 'Uploading…' : 'Upload evidence' }}</button>
        <button type="button" [disabled]="loading()" (click)="reload()">Refresh status</button>
      </form>

      @if (error(); as message) {
        <p role="alert">{{ message }}</p>
      }

      @if (loading()) {
        <p role="status">Loading evidence…</p>
      } @else if (items().length === 0) {
        <p>No evidence has been uploaded yet.</p>
      } @else {
        <ul aria-label="Uploaded evidence">
          @for (item of items(); track item.evidenceId) {
            <li>
              {{ typeLabel(item.evidenceType) }} — {{ item.fileName }} ({{ sizeLabel(item.sizeBytes) }}) —
              <strong>{{ statusLabel(item) }}</strong>
            </li>
          }
        </ul>
        <p>{{ cleanCount() }} of {{ items().length }} item(s) are CLEAN.</p>
      }
    </section>
  `,
})
export class WorkEvidencePanelComponent {
  readonly workOrderId = input.required<string>();

  private readonly evidence = inject(WorkEvidenceService);

  protected readonly evidenceTypes = EVIDENCE_TYPES;
  protected readonly accept = EVIDENCE_ACCEPT;

  protected readonly items = signal<WorkEvidenceItem[]>([]);
  protected readonly loading = signal(false);
  protected readonly uploading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly evidenceType = signal<EvidenceType>('AFTER_WORK');
  protected readonly file = signal<File | null>(null);

  protected readonly cleanCount = computed(() => this.items().filter((item) => item.scanStatus === 'CLEAN').length);
  protected readonly canUpload = computed(() => this.file() !== null && !this.uploading() && !this.loading());

  private started = false;

  /** Loads the list the first time the panel is shown; the host renders the panel only when the Technician asks for it. */
  ngOnInit(): void {
    if (!this.started) {
      this.started = true;
      this.reload();
    }
  }

  protected reload(): void {
    this.loading.set(true);
    this.error.set(null);

    this.evidence.list(this.workOrderId()).subscribe({
      next: (list) => {
        this.items.set(list.items);
        this.loading.set(false);
      },
      error: (response: HttpErrorResponse) => {
        this.loading.set(false);
        this.error.set(describe(response, 'The evidence list could not be loaded.'));
      },
    });
  }

  protected onFileChosen(files: FileList | null): void {
    this.error.set(null);
    const chosen = files && files.length > 0 ? files[0] : null;

    if (chosen !== null && chosen.size > EVIDENCE_MAX_BYTES) {
      this.file.set(null);
      this.error.set('The file must not exceed 10 MB.');
      return;
    }

    this.file.set(chosen);
  }

  protected onUpload(event: Event): void {
    event.preventDefault();
    const chosen = this.file();
    if (chosen === null || this.uploading()) {
      return;
    }

    this.uploading.set(true);
    this.error.set(null);

    this.evidence.upload(this.workOrderId(), chosen, this.evidenceType()).subscribe({
      next: (item) => {
        this.items.update((current) => [...current, item]);
        this.file.set(null);
        this.uploading.set(false);
      },
      error: (response: HttpErrorResponse) => {
        this.uploading.set(false);
        this.error.set(describe(response, 'The evidence could not be uploaded.'));
      },
    });
  }

  protected typeLabel(type: EvidenceType): string {
    return this.evidenceTypes.find((option) => option.code === type)?.label ?? type;
  }

  protected sizeLabel(bytes: number): string {
    return bytes >= 1024 * 1024 ? `${(bytes / (1024 * 1024)).toFixed(1)} MB` : bytes >= 1024 ? `${Math.round(bytes / 1024)} KB` : `${bytes} B`;
  }

  protected statusLabel(item: WorkEvidenceItem): string {
    if (item.bound) {
      return 'CLEAN — part of the submitted Work Summary';
    }

    switch (item.scanStatus) {
      case 'CLEAN':
        return 'CLEAN';
      case 'FAILED':
        return 'FAILED — does not count';
      default:
        return 'PENDING scan — does not count yet';
    }
  }
}

function describe(response: HttpErrorResponse, fallback: string): string {
  const problem = response.error as { detail?: string; errors?: Record<string, string[]> } | null;

  switch (response.status) {
    case 401:
      return 'You are not signed in, or your session has expired. Please sign in again.';
    case 403:
      return 'You do not have permission to upload evidence.';
    case 404:
      return 'This Work Order was not found, or it is not assigned to you.';
    case 409:
      return problem?.detail ?? 'Evidence cannot be uploaded now: the Work Order is not in progress, or this round is already submitted.';
    case 422: {
      const first = problem?.errors ? Object.values(problem.errors).flat()[0] : undefined;
      return first ?? 'The file or the evidence type is not valid.';
    }
    default:
      return fallback;
  }
}
