/**
 * Time Correction (UC-TIME-001; `docs/13` §4.29; `docs/17`). The shapes mirror the API exactly; the client never sets a status, the old value,
 * the requester or any tenant/SLA member — those come from the server.
 */

/** The four supported timestamps (`docs/17` §1A item 1). PAUSE_START and RESUME must name the exact pause. */
export type TimestampType = 'CHECK_IN' | 'PAUSE_START' | 'RESUME' | 'CHECK_OUT';

export interface TimestampTypeOption {
  code: TimestampType;
  label: string;
  /** PAUSE_START and RESUME name the exact pause (`workSessionPauseId`); CHECK_IN and CHECK_OUT must not send one. */
  needsPause: boolean;
}

export const TIMESTAMP_TYPES: ReadonlyArray<TimestampTypeOption> = [
  { code: 'CHECK_IN', label: 'Check-in time', needsPause: false },
  { code: 'PAUSE_START', label: 'Pause start time', needsPause: true },
  { code: 'RESUME', label: 'Resume time', needsPause: true },
  { code: 'CHECK_OUT', label: 'Check-out time', needsPause: false },
];

export function timestampTypeLabel(code: string): string {
  return TIMESTAMP_TYPES.find((type) => type.code === code)?.label ?? code;
}

/** Request and Approve are refused for these Work Order states (Reject is not). Convenience only: the backend decides. */
export const TERMINAL_WORK_ORDER_STATUSES: ReadonlyArray<string> = ['CLOSED', 'CANCELLED'];

/** `TimeCorrection.ReasonMaxLength` on the backend (request reason and decision reason). */
export const REASON_MAX_LENGTH = 1000;

/** One pause of a Work Session as a correction target (TIME-API-ADD-002). All instants are UTC. */
export interface TimeCorrectionTargetPause {
  workSessionPauseId: string;
  pausedAt: string;
  resumedAt: string | null;
}

/** One Work Session the caller may request a correction for (TIME-API-ADD-002); completed sessions are included. */
export interface TimeCorrectionTargetSession {
  workSessionId: string;
  serviceVisitId: string;
  status: string;
  checkInAt: string;
  checkOutAt: string | null;
  pauses: TimeCorrectionTargetPause[];
}

/** TIME-API-ADD-002: one Work Order's sessions inside the caller's request scope, with the pause ids and the Work Order's status. */
export interface TimeCorrectionTargets {
  workOrderId: string;
  workOrderNo: string;
  workOrderStatus: string;
  siteCode: string | null;
  equipmentCode: string | null;
  sessions: TimeCorrectionTargetSession[];
}

/** TIME-API-001 body. `newValue` is an ISO-8601 instant with an explicit offset (the client always sends `Z`). */
export interface RequestTimeCorrectionBody {
  timestampType: TimestampType;
  workSessionPauseId?: string;
  newValue: string;
  reason: string;
}

/** The Time Correction response (Request, Approve, Reject, pending list). `rowVersion` is the correction's own If-Match token. */
export interface TimeCorrection {
  timeCorrectionId: string;
  workSessionId: string;
  workSessionPauseId: string | null;
  serviceVisitId: string;
  workOrderId: string;
  workOrderNo: string;
  siteCode: string | null;
  equipmentCode: string | null;
  timestampType: string;
  oldValue: string;
  newValue: string;
  reason: string;
  requestedBy: string;
  status: string;
  decidedBy: string | null;
  decisionReason: string | null;
  decidedAt: string | null;
  rowVersion: string;
}

export interface PagedTimeCorrections {
  items: TimeCorrection[];
  page: number;
  pageSize: number;
  totalCount: number;
}
