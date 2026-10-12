/**
 * Work Order evidence (WEV-API-001/002; `docs/13` §4.28; `docs/16` OD1). The closed catalog has exactly three values; the shape mirrors the
 * API: no storage reference, content hash, tenant or uploader identity.
 */
export type EvidenceType = 'BEFORE_WORK' | 'AFTER_WORK' | 'OTHER';

export interface EvidenceTypeOption {
  code: EvidenceType;
  label: string;
}

export const EVIDENCE_TYPES: readonly EvidenceTypeOption[] = [
  { code: 'BEFORE_WORK', label: 'Before work' },
  { code: 'AFTER_WORK', label: 'After work' },
  { code: 'OTHER', label: 'Other' },
];

export type EvidenceScanStatus = 'PENDING' | 'CLEAN' | 'FAILED';

/** One evidence item (WEV-API-002). `bound` is true once a submitted Work Summary froze it. */
export interface WorkEvidenceItem {
  evidenceId: string;
  serviceVisitId: string;
  evidenceType: EvidenceType;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  scanStatus: EvidenceScanStatus;
  bound: boolean;
  workSummaryId: string | null;
  slotNo: number | null;
  uploadedAt: string;
}

export interface WorkEvidenceList {
  items: WorkEvidenceItem[];
}

/** Evidence files are PDF, JPG, JPEG or PNG of at most 10 MB — the same policy the server enforces (it is re-checked there). */
export const EVIDENCE_ACCEPT = '.pdf,.jpg,.jpeg,.png';
export const EVIDENCE_MAX_BYTES = 10 * 1024 * 1024;
