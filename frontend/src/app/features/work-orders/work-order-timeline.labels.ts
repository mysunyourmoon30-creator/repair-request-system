/** Reader-friendly labels for every audit action code a Work Order timeline can contain (`docs/15` §4). */
const LABELS: Readonly<Record<string, string>> = {
  REPAIR_REQUEST_CONVERTED: 'Work Order created from the approved request',
  WORK_ORDER_SCHEDULED: 'Work Order scheduled',
  WORK_ORDER_STARTED: 'Work started',
  WORK_ORDER_REWORK_STARTED: 'Rework started',
  SERVICE_VISIT_CHECKED_IN: 'Technician checked in',
  SERVICE_VISIT_COMPLETED: 'Service visit completed',
  SERVICE_VISIT_RESCHEDULED: 'Service visit rescheduled',
  SERVICE_VISIT_REASSIGNED: 'Service visit reassigned',
  SERVICE_VISIT_CANCELLED: 'Service visit cancelled',
  SERVICE_VISIT_MARKED_MISSED: 'Service visit marked missed',
  SERVICE_VISIT_MISSED_DECIDED: 'Follow-up decision recorded for a missed visit',
  WORK_SESSION_PAUSED: 'Work session paused',
  WORK_SESSION_RESUMED: 'Work session resumed',
  WORK_SESSION_CHECKED_OUT: 'Technician checked out',
  WORK_SUMMARY_SUBMITTED: 'Work summary submitted',
  WORK_ORDER_SUBMITTED_FOR_ACCEPTANCE: 'Submitted for customer acceptance',
  WORK_ORDER_ACCEPTED: 'Customer accepted the work',
  // The customer's rejection of the work — not to be confused with a Repair Request being rejected.
  WORK_ORDER_REJECTED: 'Customer rejected work',
  COST_SUMMARY_PREPARED: 'Cost summary prepared',
  COST_SUMMARY_UPDATED: 'Cost summary updated',
  COST_SUMMARY_REVIEWED: 'Cost summary reviewed',
  WORK_ORDER_CLOSED: 'Work Order closed',
  WORK_ORDER_CANCELLED: 'Work Order cancelled',
  CORRECTIVE_ACTION_PLAN_SUBMITTED: 'Corrective plan submitted',
  CORRECTIVE_ACTION_PLAN_APPROVED: 'Corrective plan approved',
  CORRECTIVE_ACTION_REWORK_SCHEDULED: 'Rework visit scheduled',
};

/** Label for an action code; an unknown (future) code falls back to a humanized form instead of breaking the page. */
export function timelineActionLabel(actionCode: string): string {
  const known = LABELS[actionCode];
  if (known !== undefined) {
    return known;
  }

  const words = actionCode.toLowerCase().replace(/[_\s]+/g, ' ').trim();
  return words.length === 0 ? 'Activity' : words.charAt(0).toUpperCase() + words.slice(1);
}

/** `serviceVisitId` -> `Service visit id`. */
export function timelineDetailLabel(key: string): string {
  const spaced = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}
