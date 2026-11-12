import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';

import { language } from '../i18n/language';
import { AuthService, SKIP_AUTH } from './auth.service';

function withToken(request: HttpRequest<unknown>, token: string | null): HttpRequest<unknown> {
  return token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;
}

/**
 * A 401 means "refresh and retry" only when the token itself was rejected. Coded business 401s
 * (e.g. Auth.AccountLocked on change-password) go straight to the caller.
 */
function isExpiredToken(error: HttpErrorResponse): boolean {
  const code = (error.error as { code?: string } | null)?.code;
  return code === undefined || code === 'Auth.Unauthenticated' || code === 'Auth.SessionExpired';
}

/**
 * Adds the in-memory access token to same-origin API calls. On a 401 it retries with a newer token
 * if another call already refreshed, otherwise refreshes once and retries. Only a rejected refresh
 * cookie ends the session; a network or server error during refresh is passed to the caller.
 */
export const authInterceptor: HttpInterceptorFn = (original, next) => {
  const auth = inject(AuthService);
  if (!original.url.startsWith('/api/')) {
    return next(original);
  }
  // P3-10: problem details come back in the UI language (title/detail); the error codes never change.
  const request = original.clone({ setHeaders: { 'Accept-Language': language() } });
  if (request.context.get(SKIP_AUTH)) {
    return next(request);
  }

  // Captured when the request is sent: a 401 for a request of an older session (e.g. the startup restore's /me
  // while a login waits) must not refresh, retry with the new session's token or expire the new session.
  const sentEpoch = auth.epoch();
  const sentToken = auth.token();
  return next(withToken(request, sentToken)).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || !isExpiredToken(error)) {
        return throwError(() => error);
      }
      if (auth.epoch() !== sentEpoch) {
        return throwError(() => error);
      }
      const current = auth.token();
      if (current && current !== sentToken) {
        // Another request refreshed while this one was on the wire.
        return next(withToken(request, current));
      }
      return auth.refresh().pipe(
        switchMap((token) => {
          if (!token) {
            // Skip the redirect when the user already logged out (or the refresh expired the session) meanwhile.
            if (auth.epoch() === sentEpoch) {
              auth.expireSession();
            }
            return throwError(() => error);
          }
          return next(withToken(request, token));
        }),
      );
    }),
  );
};
