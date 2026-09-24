import { getCurrentUserId, getCurrentUserRoles } from './current-user-role';

function tokenWithPayload(payload: Record<string, unknown>): string {
  const base64url = (value: string) => btoa(value).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${base64url('{"alg":"HS256"}')}.${base64url(JSON.stringify(payload))}.signature`;
}

describe('getCurrentUserRoles', () => {
  afterEach(() => localStorage.clear());

  it('returns an empty array when there is no token', () => {
    expect(getCurrentUserRoles()).toEqual([]);
  });

  it('returns a single role as a one-item array', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'u1', role: 'TECHNICIAN' }));
    expect(getCurrentUserRoles()).toEqual(['TECHNICIAN']);
  });

  it('returns multiple roles as-is', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'u1', role: ['REQUESTER', 'APPROVER'] }));
    expect(getCurrentUserRoles()).toEqual(['REQUESTER', 'APPROVER']);
  });

  it('returns an empty array for a malformed token', () => {
    localStorage.setItem('accessToken', 'not-a-jwt');
    expect(getCurrentUserRoles()).toEqual([]);
  });

  it('returns an empty array when the payload has no role claim', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'u1' }));
    expect(getCurrentUserRoles()).toEqual([]);
  });
});

describe('getCurrentUserId', () => {
  afterEach(() => localStorage.clear());

  it('returns null when there is no token', () => {
    expect(getCurrentUserId()).toBeNull();
  });

  it('returns the sub claim', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ sub: 'user-123', role: 'REQUESTER' }));
    expect(getCurrentUserId()).toBe('user-123');
  });

  it('returns null for a malformed token', () => {
    localStorage.setItem('accessToken', 'not-a-jwt');
    expect(getCurrentUserId()).toBeNull();
  });

  it('returns null when the payload has no sub claim', () => {
    localStorage.setItem('accessToken', tokenWithPayload({ role: 'REQUESTER' }));
    expect(getCurrentUserId()).toBeNull();
  });
});
