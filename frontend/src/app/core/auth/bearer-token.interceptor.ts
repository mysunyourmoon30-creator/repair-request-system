import { HttpInterceptorFn } from '@angular/common/http';

const ACCESS_TOKEN_KEY = 'accessToken';

/**
 * Attaches `Authorization: Bearer <token>` to every outgoing request when an access token is
 * present in `localStorage`. This is deliberately the only piece of the auth flow implemented so
 * far — there is still no login page and nothing writes to `accessToken` from within the app.
 * Without a token, requests go out exactly as before (protected routes correctly stay 401).
 */
export const bearerTokenInterceptor: HttpInterceptorFn = (request, next) => {
  let token: string | null = null;
  try {
    token = localStorage.getItem(ACCESS_TOKEN_KEY);
  } catch {
    // localStorage can throw (private browsing, blocked storage); treat as "no token".
  }

  if (!token) {
    return next(request);
  }

  return next(request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
