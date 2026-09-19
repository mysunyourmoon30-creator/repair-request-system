const ACCESS_TOKEN_KEY = 'accessToken';

/**
 * Reads the `role` claim(s) out of the access token in `localStorage`, purely for client-side UX
 * (e.g. hiding a nav link, redirecting away from a page that doesn't apply to this role). This is
 * NOT a security boundary — the token is never verified here, and a caller can trivially edit
 * `localStorage`. The backend re-derives role from the identity store on every request and is the
 * only real authorization boundary (see `JwtAccessTokenIssuer`'s and `CurrentUserStore`'s own doc
 * comments: "Role claims in the access token are not trusted" server-side). `role` may appear as a
 * single string or, for a multi-role user, a JSON array — both are normalized here.
 */
export function getCurrentUserRoles(): string[] {
  let token: string | null = null;
  try {
    token = localStorage.getItem(ACCESS_TOKEN_KEY);
  } catch {
    return [];
  }

  if (!token) {
    return [];
  }

  const payload = decodeJwtPayload(token);
  const role = payload?.['role'];
  if (typeof role === 'string') {
    return [role];
  }

  if (Array.isArray(role)) {
    return role.filter((item): item is string => typeof item === 'string');
  }

  return [];
}

function decodeJwtPayload(token: string): Record<string, unknown> | null {
  try {
    const [, payloadSegment] = token.split('.');
    if (!payloadSegment) {
      return null;
    }

    const base64 = payloadSegment.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(payloadSegment.length / 4) * 4, '=');
    return JSON.parse(atob(base64)) as Record<string, unknown>;
  } catch {
    return null;
  }
}
