/** One pause period of a Work Session (S3-002). Matches `WorkSessionPauseResponse` on the backend; newest first. */
export interface WorkSessionPause {
  workSessionPauseId: string;
  pausedAt: string;
  pauseReason: string;
  resumedAt: string | null;
}

/**
 * The Technician's own Work Session (S3-002; RR-DD-001 WS-001..010). Matches `WorkSessionResponse` on the backend
 * exactly. `rowVersion` is the session's own token — the If-Match value of Pause. `workOrderRowVersion` is the
 * parent Work Order's own token — the If-Match value of Submit Work Summary once `status` is `CHECKED_OUT`
 * (`docs/13` §4.15; a Technician has no other way to reach it). `status` is the canonical code (`CHECKED_IN`,
 * `PAUSED`, `CHECKED_OUT`); the UI never sets it, it only reads what the backend returns.
 */
export interface WorkSession {
  workSessionId: string;
  serviceVisitId: string;
  workOrderId: string;
  workOrderNo: string;
  siteCode: string | null;
  equipmentCode: string | null;
  status: string;
  checkInAt: string;
  pauseStartAt: string | null;
  resumeAt: string | null;
  checkOutAt: string | null;
  pauses: WorkSessionPause[];
  rowVersion: string;
  workOrderRowVersion: string;
}
