/**
 * Time handling of the Time Correction screens. The API sends and accepts UTC instants. Following the existing screens (the Service Report
 * labels every time "(UTC)"; the schedule form treats a `datetime-local` value as UTC), the screens show and accept UTC, labeled as such, and
 * show the browser-local equivalent next to it so nobody has to convert in their head. `newValue` is always sent with an explicit `Z`.
 */

const HAS_ZONE = /(?:[zZ]|[+-]\d{2}:?\d{2})$/;
const DATETIME_LOCAL = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/;

/** Parses an API instant; a value without any offset is read as UTC (the API sends UTC), never as local time. Returns null when it is not a date. */
export function parseUtc(value: string | null | undefined): Date | null {
  if (!value) {
    return null;
  }

  const date = new Date(HAS_ZONE.test(value) ? value : `${value}Z`);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** `2026-10-14 08:20:00 UTC`. */
export function formatUtc(value: string | null | undefined): string {
  const date = parseUtc(value);
  return date === null ? '—' : `${date.toISOString().slice(0, 19).replace('T', ' ')} UTC`;
}

/** The same instant in the browser's time zone, with the zone's name. */
export function formatLocal(value: string | null | undefined): string {
  const date = parseUtc(value);
  if (date === null) {
    return '—';
  }

  let zone = '';
  try {
    zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  } catch {
    zone = '';
  }

  return zone ? `${date.toLocaleString()} (${zone})` : date.toLocaleString();
}

/** The ISO-8601 instant to send for a `datetime-local` value, which is entered as UTC. Null when the text is not a complete, real date-time. */
export function datetimeLocalToUtcIso(value: string): string | null {
  const match = DATETIME_LOCAL.exec(value.trim());
  if (match === null) {
    return null;
  }

  const [, year, month, day, hour, minute, second = '00'] = match;
  const iso = `${year}-${month}-${day}T${hour}:${minute}:${second}.000Z`;
  const date = new Date(iso);
  // A rolled-over value (for example 2026-02-31) is not a real date-time.
  return Number.isNaN(date.getTime()) || date.toISOString() !== iso ? null : iso;
}

/** The `datetime-local` text (UTC components, seconds included) of an API instant, for pre-filling the input. */
export function utcToDatetimeLocal(value: string | null | undefined): string {
  const date = parseUtc(value);
  return date === null ? '' : date.toISOString().slice(0, 19);
}

/** True when the instant is later than `now`. The API refuses a new value in the future (equal is allowed). */
export function isAfter(value: string, now: number = Date.now()): boolean {
  const date = parseUtc(value);
  return date !== null && date.getTime() > now;
}

/** True when both instants are the same moment (millisecond precision, as the API compares them). */
export function sameInstant(a: string | null | undefined, b: string | null | undefined): boolean {
  const first = parseUtc(a);
  const second = parseUtc(b);
  return first !== null && second !== null && first.getTime() === second.getTime();
}
