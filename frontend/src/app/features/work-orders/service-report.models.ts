import { EvidenceType } from './work-evidence.models';

/**
 * SVR-API-001 Service Report (`docs/13` §4.28; `docs/16` D1, OD4/OD5). The shape mirrors the API exactly. There is **no** `costSummary`
 * member on {@link ServiceReport}: the server sends it only to an in-scope Team Lead or Supervisor ({@link ServiceReportWithCost}); for the
 * Technician and the Acceptance Contact the property is absent from the JSON, and the client never constructs or infers cost data.
 * There is deliberately no storage reference, content hash, tenant, e-mail or uploader identity.
 */
export interface ServiceReportHeader {
  workOrderId: string;
  workOrderNo: string;
  status: string;
  createdAt: string;
  closedAt: string | null;
  repairRequestId: string;
  repairRequestNo: string | null;
  customerCode: string | null;
  siteCode: string | null;
  equipmentCode: string | null;
}

export interface ServiceReportPause {
  pausedAt: string;
  resumedAt: string | null;
}

export interface ServiceReportSession {
  workSessionId: string;
  status: string;
  checkInAt: string;
  checkOutAt: string | null;
  pauses: ServiceReportPause[];
}

export interface ServiceReportVisit {
  serviceVisitId: string;
  visitType: string;
  status: string;
  assignedTechnicianId: string | null;
  scheduledStartAt: string | null;
  scheduledEndAt: string | null;
  completedAt: string | null;
  sessions: ServiceReportSession[];
}

/** One CLEAN evidence item frozen at Submit; its content is downloaded through WEV-API-003 only. */
export interface ServiceReportEvidence {
  evidenceId: string;
  evidenceType: EvidenceType;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  slotNo: number;
  boundAt: string;
}

export interface ServiceReportSummary {
  workSummaryId: string;
  serviceVisitId: string;
  revisionNo: number;
  summaryText: string;
  repairOutcomeCode: string;
  evidence: ServiceReportEvidence[];
}

export interface ServiceReportAcceptance {
  roundNo: number;
  decision: 'ACCEPT' | 'REJECT';
  decisionReason: string | null;
  decidedAt: string;
  acceptanceContactId: string;
}

export interface ServiceReportCost {
  totalAmount: number;
  currencyCode: string;
  note: string | null;
  preparedBy: string;
  preparedAt: string;
  reviewedBy: string | null;
  reviewedAt: string | null;
}

/** The operational report (Technician, Acceptance Contact): no cost member at all. */
export interface ServiceReport {
  workOrder: ServiceReportHeader;
  visits: ServiceReportVisit[];
  workSummaries: ServiceReportSummary[];
  acceptance: ServiceReportAcceptance[];
}

/** The full report (Team Lead, Supervisor): `costSummary` is null until a Cost Summary has been prepared. */
export interface ServiceReportWithCost extends ServiceReport {
  costSummary: ServiceReportCost | null;
}

/** The response of any audience: the cost section exists only when the property is present in the JSON. */
export type ServiceReportResponse = ServiceReport | ServiceReportWithCost;

/** True only when the server included the cost section — the sole source of truth for showing it. */
export function hasCostSection(report: ServiceReportResponse): report is ServiceReportWithCost {
  return Object.prototype.hasOwnProperty.call(report, 'costSummary');
}
