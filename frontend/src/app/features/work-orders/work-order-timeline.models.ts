/**
 * AUD-API-001 Work Order timeline (UC-WO-002; `docs/15` §4). The shape mirrors the API exactly: there is deliberately
 * no reason, correlation id, raw payload, name or e-mail — `details` carries only allowlisted scalar values.
 */
export type AuditActorKind = 'USER' | 'SYSTEM' | 'UNKNOWN';

export interface AuditTimelineActor {
  actorId: string;
  kind: AuditActorKind;
}

export type AuditTimelineDetailValue = string | number | boolean | null;

export interface AuditTimelineItem {
  auditId: string;
  /** UTC, ISO-8601 with a `Z` suffix. */
  occurredAt: string;
  actionCode: string;
  entityType: string;
  entityId: string;
  fromState: string | null;
  toState: string | null;
  actor: AuditTimelineActor;
  details: Record<string, AuditTimelineDetailValue>;
}

/** Keyset page: `nextCursor` is null exactly when `hasMore` is false. */
export interface AuditTimelinePage {
  items: AuditTimelineItem[];
  nextCursor: string | null;
  hasMore: boolean;
}
