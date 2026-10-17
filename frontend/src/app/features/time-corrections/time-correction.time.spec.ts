import {
  datetimeLocalToUtcIso,
  formatLocal,
  formatUtc,
  isAfter,
  parseUtc,
  sameInstant,
  utcToDatetimeLocal,
} from './time-correction.time';

describe('time-correction time helpers', () => {
  it('reads an API instant with a Z, an offset or no offset at all; none of them is read as local time', () => {
    const expected = Date.UTC(2026, 9, 14, 8, 20, 0);

    expect(parseUtc('2026-10-14T08:20:00Z')?.getTime()).toBe(expected);
    expect(parseUtc('2026-10-14T08:20:00')?.getTime()).toBe(expected); // no offset: UTC, whatever the machine's zone
    expect(parseUtc('2026-10-14T08:20:00.1234567')?.getTime()).toBe(expected + 123);
    expect(parseUtc('2026-10-14T15:20:00+07:00')?.getTime()).toBe(expected);
  });

  it('returns null for a missing or unparseable value', () => {
    expect(parseUtc(null)).toBeNull();
    expect(parseUtc(undefined)).toBeNull();
    expect(parseUtc('')).toBeNull();
    expect(parseUtc('not a date')).toBeNull();
    expect(formatUtc('not a date')).toBe('—');
    expect(formatLocal(null)).toBe('—');
  });

  it('formats a UTC instant with an explicit UTC label', () => {
    expect(formatUtc('2026-10-14T08:20:00Z')).toBe('2026-10-14 08:20:00 UTC');
    expect(formatUtc('2026-10-14T08:20:00')).toBe('2026-10-14 08:20:00 UTC');
  });

  it('shows the browser-local rendering of the same instant, naming the zone when it is known', () => {
    const text = formatLocal('2026-10-14T08:20:00Z');

    expect(text).toContain(new Date('2026-10-14T08:20:00Z').toLocaleString());
  });

  it('turns a datetime-local value, entered as UTC, into an ISO instant with an explicit Z', () => {
    expect(datetimeLocalToUtcIso('2026-10-14T08:25')).toBe('2026-10-14T08:25:00.000Z');
    expect(datetimeLocalToUtcIso('2026-10-14T08:25:30')).toBe('2026-10-14T08:25:30.000Z');
    expect(datetimeLocalToUtcIso(' 2026-10-14T08:25 ')).toBe('2026-10-14T08:25:00.000Z');
  });

  it('refuses an incomplete or impossible date-time', () => {
    expect(datetimeLocalToUtcIso('')).toBeNull();
    expect(datetimeLocalToUtcIso('2026-10-14')).toBeNull();
    expect(datetimeLocalToUtcIso('2026-10-14T08')).toBeNull();
    expect(datetimeLocalToUtcIso('2026-02-31T08:00')).toBeNull();
    expect(datetimeLocalToUtcIso('2026-10-14T25:00')).toBeNull();
    expect(datetimeLocalToUtcIso('2026-10-14T08:25Z')).toBeNull();
  });

  it('round-trips an instant through the input text without drifting by the local offset', () => {
    const text = utcToDatetimeLocal('2026-10-14T08:20:00Z');

    expect(text).toBe('2026-10-14T08:20:00');
    expect(datetimeLocalToUtcIso(text)).toBe('2026-10-14T08:20:00.000Z');
    expect(utcToDatetimeLocal(null)).toBe('');
  });

  it('compares with now (equal is not later) and by instant', () => {
    const now = Date.UTC(2026, 9, 14, 8, 20, 0);

    expect(isAfter('2026-10-14T08:20:01Z', now)).toBe(true);
    expect(isAfter('2026-10-14T08:20:00Z', now)).toBe(false);
    expect(isAfter('garbage', now)).toBe(false);
    expect(sameInstant('2026-10-14T08:20:00Z', '2026-10-14T08:20:00.000')).toBe(true);
    expect(sameInstant('2026-10-14T08:20:00Z', '2026-10-14T08:20:01Z')).toBe(false);
    expect(sameInstant(null, '2026-10-14T08:20:00Z')).toBe(false);
  });
});
