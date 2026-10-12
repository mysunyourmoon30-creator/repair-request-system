import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { EVIDENCE_TYPES, EvidenceType, WorkEvidenceItem } from '../work-orders/work-evidence.models';
import { WorkEvidenceService } from '../work-orders/work-evidence.service';

/** The most evidence items one Work Summary can freeze (`docs/16` OD1; the server enforces it). */
export const MAX_SELECTED_EVIDENCE = 10;

/**
 * Evidence selection of Submit/Resubmit Work Summary (`docs/13` §4.28; `docs/16` OD1). It lists the evidence of the current Service Visit that is
 * not yet frozen — the Technician names exactly the CLEAN items to submit; nothing is chosen automatically. PENDING and FAILED items stay
 * visible with their status but are disabled, a CLEAN item can be selected once, and at most ten can be selected. The selected ids, in
 * selection order (which becomes the slot order), are emitted to the host, which sends them as <c>evidenceIds</c>. The backend re-validates
 * every id (tenant, Work Order, Visit, scan status, already bound) and stays authoritative.
 */
@Component({
  selector: 'app-work-evidence-selection',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fieldset>
      <legend>Evidence (select 1 to {{ max }} CLEAN items)</legend>

      @if (loading()) {
        <p role="status">Loading evidence…</p>
      } @else if (error(); as message) {
        <p role="alert">{{ message }}</p>
      } @else if (candidates().length === 0) {
        <p>No evidence has been uploaded for this visit yet. Use Evidence to upload at least one file, then Refresh status.</p>
      } @else {
        <ul aria-label="Evidence of this visit">
          @for (item of candidates(); track item.evidenceId) {
            <li>
              <label>
                <input
                  type="checkbox"
                  [checked]="isSelected(item)"
                  [disabled]="!selectable(item)"
                  (change)="toggle(item)"
                />
                {{ typeLabel(item.evidenceType) }} — {{ item.fileName }} ({{ sizeLabel(item.sizeBytes) }}) —
                <strong>{{ statusLabel(item) }}</strong>
              </label>
            </li>
          }
        </ul>
        <p>{{ selected().length }} of {{ max }} selected.</p>
      }

      <button type="button" [disabled]="loading()" (click)="reload()">Refresh status</button>
    </fieldset>
  `,
})
export class WorkEvidenceSelectionComponent implements OnInit {
  readonly workOrderId = input.required<string>();
  readonly serviceVisitId = input.required<string>();

  /** Selected evidence ids in selection order; emitted whenever the selection changes. */
  readonly selectionChange = output<string[]>();

  private readonly evidence = inject(WorkEvidenceService);

  protected readonly max = MAX_SELECTED_EVIDENCE;
  protected readonly items = signal<WorkEvidenceItem[]>([]);
  protected readonly selected = signal<string[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  /** Not yet frozen evidence of the current Visit — every status, so PENDING and FAILED stay visible. */
  protected readonly candidates = computed(() =>
    this.items().filter((item) => item.serviceVisitId === this.serviceVisitId() && !item.bound),
  );

  ngOnInit(): void {
    this.reload();
  }

  protected reload(): void {
    this.loading.set(true);
    this.error.set(null);

    this.evidence.list(this.workOrderId()).subscribe({
      next: (list) => {
        this.items.set(list.items);
        this.loading.set(false);
        // Keep only selections that are still selectable (CLEAN, not frozen, of this Visit).
        const usable = new Set(this.candidates().filter((item) => item.scanStatus === 'CLEAN').map((item) => item.evidenceId));
        this.commit(this.selected().filter((id) => usable.has(id)));
      },
      error: (response: HttpErrorResponse) => {
        this.loading.set(false);
        this.error.set(
          response.status === 401 || response.status === 403
            ? 'You do not have permission to view this evidence.'
            : 'The evidence list could not be loaded. Try Refresh status.',
        );
      },
    });
  }

  protected isSelected(item: WorkEvidenceItem): boolean {
    return this.selected().includes(item.evidenceId);
  }

  /** Only CLEAN items can be selected; a selected item can always be unselected; no more than ten at a time. */
  protected selectable(item: WorkEvidenceItem): boolean {
    if (this.isSelected(item)) {
      return true;
    }

    return item.scanStatus === 'CLEAN' && this.selected().length < MAX_SELECTED_EVIDENCE;
  }

  protected toggle(item: WorkEvidenceItem): void {
    if (this.isSelected(item)) {
      this.commit(this.selected().filter((id) => id !== item.evidenceId));
      return;
    }

    // A duplicate id can never be added, and neither can a non-CLEAN item or an eleventh one.
    if (!this.selectable(item) || this.selected().includes(item.evidenceId)) {
      return;
    }

    this.commit([...this.selected(), item.evidenceId]);
  }

  protected typeLabel(type: EvidenceType): string {
    return EVIDENCE_TYPES.find((option) => option.code === type)?.label ?? type;
  }

  protected sizeLabel(bytes: number): string {
    return bytes >= 1024 * 1024 ? `${(bytes / (1024 * 1024)).toFixed(1)} MB` : bytes >= 1024 ? `${Math.round(bytes / 1024)} KB` : `${bytes} B`;
  }

  protected statusLabel(item: WorkEvidenceItem): string {
    switch (item.scanStatus) {
      case 'CLEAN':
        return 'CLEAN';
      case 'FAILED':
        return 'FAILED — cannot be selected';
      default:
        return 'PENDING scan — cannot be selected yet';
    }
  }

  private commit(next: string[]): void {
    this.selected.set(next);
    this.selectionChange.emit(next);
  }
}
