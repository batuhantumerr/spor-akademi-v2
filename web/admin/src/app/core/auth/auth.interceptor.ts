import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';

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
 * Adds the in-memory access token to same-origin API calls. On a 401 it refreshes once and
 * retries; if the refresh fails, the session is over and the user goes back to the login page.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  if (request.context.get(SKIP_AUTH) || !request.url.startsWith('/api/')) {
    return next(request);
  }

  return next(withToken(request, auth.token())).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401 || !isExpiredToken(error)) {
        return throwError(() => error);
      }
      return auth.refresh().pipe(
        switchMap((token) => {
          if (!token) {
            auth.expireSession();
            return throwError(() => error);
          }
          return next(withToken(request, token));
        }),
      );
    }),
  );
};
