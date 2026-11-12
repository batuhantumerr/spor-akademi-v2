import {
  HttpClient,
  HttpContext,
  HttpContextToken,
  HttpErrorResponse,
  HttpHeaders,
} from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import {
  Observable,
  Subscription,
  catchError,
  defer,
  finalize,
  map,
  from,
  of,
  race,
  retry,
  shareReplay,
  switchMap,
  take,
  tap,
  throwError,
  timer,
} from 'rxjs';

/** 'Parent' (P3-01) only ever arrives from the API; the admin panel ends such a session at once. */
export type Role = 'PlatformAdmin' | 'SchoolAdmin' | 'Coach' | 'Parent';

export interface CurrentUser {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  role: Role;
  tenantId: string | null;
  mustChangePassword: boolean;
  /** IANA zone of the user's school (null for the Platform Admin); "today" is computed in it. */
  schoolTimeZone?: string | null;
}

interface TokenResponse {
  accessToken: string;
  expiresIn: number;
}

/** Web Lock that serializes refresh calls across the tabs of this origin. */
export const REFRESH_LOCK = 'sa-refresh';

/** Pause before the single startup restore retry (DEC-447). */
export const RESTORE_RETRY_DELAY_MS = 500;

/** Longest a login waits for a still-running startup restore call to settle (DEC-447). */
export const LOGIN_RESTORE_WAIT_MS = 15000;

/** Marks requests that must not get a bearer token or trigger a refresh retry. */
export const SKIP_AUTH = new HttpContextToken<boolean>(() => false);

/**
 * Session state (ADR-0006): the access token lives only in memory; the refresh token is an
 * HttpOnly cookie the browser sends to /api/v1/auth. A page reload restores the session through
 * a refresh call. Refresh and logout carry X-Requested-With (DEC-055).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly accessToken = signal<string | null>(null);
  private readonly currentUser = signal<CurrentUser | null>(null);
  private refreshInFlight: Observable<string | null> | null = null;
  /**
   * Bumped whenever the session is ended on purpose (logout, expiry). Refresh and /me results
   * that started under an older epoch are dropped, so a slow refresh cannot revive a session.
   */
  private sessionEpoch = 0;
  /** Settles when the startup restore (and its refresh call) has finished; null when none is running. */
  private bootRestore: Promise<void> | null = null;

  readonly user = this.currentUser.asReadonly();
  readonly isAuthenticated = computed(() => this.currentUser() !== null);
  readonly role = computed(() => this.currentUser()?.role ?? null);
  readonly mustChangePassword = computed(() => this.currentUser()?.mustChangePassword ?? false);

  token(): string | null {
    return this.accessToken();
  }

  /** Identifies the current session; changes on login, logout and expiry. */
  epoch(): number {
    return this.sessionEpoch;
  }

  /**
   * Signs in. A startup restore still running (DEC-447) is abandoned first: the epoch moves on, so its late
   * result (another person's session on a shared computer) is dropped, and the login request waits until that
   * refresh call has settled, so the login's cookie is the last one the browser stores.
   */
  login(email: string, password: string): Observable<CurrentUser> {
    return defer(() => {
      this.endSession();
      const epoch = this.sessionEpoch;
      const pending = this.bootRestore;
      // A refresh call that never answers must not block signing in forever.
      const settled = pending
        ? race(from(pending), timer(LOGIN_RESTORE_WAIT_MS)).pipe(take(1))
        : of(undefined);
      return settled.pipe(
        switchMap(() =>
          this.http.post<TokenResponse>(
            '/api/v1/auth/login',
            { email, password },
            { context: skipAuth() },
          ),
        ),
        tap((response) => {
          if (epoch === this.sessionEpoch) {
            this.accessToken.set(response.accessToken);
          }
        }),
        switchMap(() => this.loadMe(epoch)),
      );
    });
  }

  /**
   * Restores the session at startup; resolves to false when there is no valid refresh cookie. Only a rejected
   * cookie (401/403) means "no session": a conflict (409, e.g. another tab rotating the same cookie), server
   * error or network failure is retried once after a short pause, since the cookie is most likely still valid
   * (DEC-447). If the retry fails too, or a 429 arrives, the app starts signed out as before. Resolves to false
   * as well when a login or logout started meanwhile (its result is stale).
   */
  restore(): Observable<boolean> {
    return defer(() => {
      const epoch = this.sessionEpoch;
      return this.refresh().pipe(
        switchMap((token) =>
          token ? this.loadMe(epoch).pipe(map(() => epoch === this.sessionEpoch)) : of(false),
        ),
        retry({
          count: 1,
          delay: (error: unknown) =>
            isTransient(error)
              ? timer(RESTORE_RETRY_DELAY_MS).pipe(
                  // A logout during the pause wins: no second attempt.
                  switchMap(() => (epoch === this.sessionEpoch ? of(0) : throwError(() => error))),
                )
              : throwError(() => error),
        }),
        catchError(() => of(false)),
      );
    });
  }

  /**
   * Startup restore for the app initializer: the app waits at most `waitMs`, then starts signed out, but the
   * refresh call is never cancelled (DEC-447). Aborting it could drop the rotated cookie the server already
   * committed. If the restore succeeds after the wait and nobody has started a login meanwhile, the user is
   * taken from the login page to where they were heading. A login started meanwhile waits for the call and
   * drops its result (see {@link login}).
   */
  restoreAtBoot(waitMs: number): Promise<boolean> {
    return new Promise<boolean>((resolve) => {
      let late = false;
      let settle!: () => void;
      const settled = new Promise<void>((done) => (settle = done));
      this.bootRestore = settled;
      const wait = setTimeout(() => {
        late = true;
        resolve(false);
      }, waitMs);
      this.restore()
        .pipe(
          finalize(() => {
            if (this.bootRestore === settled) {
              this.bootRestore = null;
            }
            settle();
          }),
        )
        .subscribe((restored) => {
          clearTimeout(wait);
          if (!late) {
            resolve(restored);
          } else if (restored) {
            // restore() only reports true while its epoch is current, i.e. no login or logout happened since.
            this.leaveLoginPage();
          }
        });
    });
  }

  /**
   * One refresh at a time; concurrent 401s wait for the same call. Emits the new token, or null
   * when the refresh cookie was rejected (401/403: the session is over). Network and server errors
   * are re-thrown without touching the session, so a flaky connection does not log the user out.
   *
   * Across tabs the call runs under an exclusive Web Lock (P3-08): each tab then rotates the cookie
   * the previous one already rotated, instead of two tabs presenting the same refresh token at once
   * (reuse detection would end the session on every device). A tab whose session ended while it
   * waited for the lock skips the call.
   *
   * While a user is signed in, the new token's `sub` must be that user's id; otherwise the session is expired
   * (sent to /login?expired=1) instead of mixing two identities. This is only a consistency check, the
   * signature is not verified client-side.
   */
  refresh(): Observable<string | null> {
    if (this.refreshInFlight) {
      return this.refreshInFlight;
    }
    const epoch = this.sessionEpoch;
    const post = this.http.post<TokenResponse>('/api/v1/auth/refresh', null, {
      headers: ajaxHeaders(),
      context: skipAuth(),
    });
    const call: Observable<string | null> = underRefreshLock(
      post,
      () => epoch !== this.sessionEpoch,
    ).pipe(
      map((response) => {
        if (!response || epoch !== this.sessionEpoch) {
          return null; // Logged out while the refresh was in flight.
        }
        const signedIn = this.currentUser();
        if (signedIn && !sameSubject(response.accessToken, signedIn.id)) {
          // The cookie belongs to someone else (e.g. a very late startup refresh of another person on a shared
          // computer stored its cookie after this login, DEC-447): never continue with a mismatched identity.
          this.expireSession();
          return null;
        }
        this.accessToken.set(response.accessToken);
        return response.accessToken;
      }),
      catchError((error: unknown) => {
        if (!endsSession(error)) {
          return throwError(() => error);
        }
        if (epoch === this.sessionEpoch) {
          this.clear();
        }
        return of(null);
      }),
      finalize(() => {
        if (this.refreshInFlight === call) {
          this.refreshInFlight = null;
        }
      }),
      shareReplay({ bufferSize: 1, refCount: true }),
    );
    this.refreshInFlight = call;
    return call;
  }

  changePassword(currentPassword: string, newPassword: string): Observable<CurrentUser> {
    return this.http
      .post<TokenResponse | null>('/api/v1/auth/change-password', { currentPassword, newPassword })
      .pipe(
        // Changing the password revokes every session; the API answers with fresh tokens.
        switchMap((response) =>
          response?.accessToken ? of(response.accessToken) : this.refresh(),
        ),
        tap((token) => token && this.accessToken.set(token)),
        switchMap(() => this.loadMe(this.sessionEpoch)),
      );
  }

  logout(): Observable<void> {
    return defer(() => {
      // End the local session first so an in-flight refresh cannot bring it back.
      this.endSession();
      return this.http.post<void>('/api/v1/auth/logout', null, {
        headers: ajaxHeaders(),
        context: skipAuth(),
      });
    }).pipe(
      catchError(() => of(undefined)),
      map(() => undefined),
      finalize(() => {
        this.clear();
        void this.router.navigate(['/login']);
      }),
    );
  }

  /**
   * Ends the session in this tab without calling the API, as logout does locally: after a password reset the server
   * has already revoked every refresh token (P2-F4), so only the in-memory state is stale.
   */
  forgetSession(): void {
    this.endSession();
  }

  /** Clears local state after an unrecoverable 401; the user comes back to where they were. */
  expireSession(): void {
    this.endSession();
    const current = this.router.url;
    const returnUrl =
      current && current !== '/' && !current.startsWith('/login') ? current : undefined;
    void this.router.navigate(['/login'], { queryParams: { expired: 1, returnUrl } });
  }

  homeFor(role: Role | null): string {
    switch (role) {
      case 'PlatformAdmin':
        return '/platform/schools';
      case 'Coach':
        return '/coach/today';
      case 'SchoolAdmin':
        return '/dashboard';
      default:
        return '/login';
    }
  }

  private loadMe(epoch: number): Observable<CurrentUser> {
    return this.http.get<CurrentUser>('/api/v1/me').pipe(
      // P3-01: parents use the parent app; their session is ended here and the cookie dropped.
      // A stale result (a login or logout happened meanwhile) is ignored: logging it out could end the newer session.
      switchMap((user) =>
        user.role === 'Parent' && epoch === this.sessionEpoch ? this.refuseParent() : of(user),
      ),
      tap((user) => {
        if (epoch === this.sessionEpoch) {
          this.currentUser.set(user);
        }
      }),
      catchError((error: unknown) => {
        if (epoch === this.sessionEpoch) {
          this.clear();
        }
        return throwError(() => error);
      }),
    );
  }

  /** A late startup restore signed the user in while the login page was already showing. */
  private leaveLoginPage(): void {
    const tree = this.router.parseUrl(this.router.url);
    const path = tree.root.children['primary']?.segments.map((s) => s.path).join('/') ?? '';
    if (path !== 'login') {
      return;
    }
    const returnUrl = tree.queryParamMap.get('returnUrl');
    const target = this.mustChangePassword()
      ? '/change-password'
      : returnUrl?.startsWith('/') && !returnUrl.startsWith('//')
        ? returnUrl
        : this.homeFor(this.role());
    void this.router.navigateByUrl(target);
  }

  private refuseParent(): Observable<never> {
    this.endSession();
    const refused = new HttpErrorResponse({
      status: 403,
      error: { code: 'ParentAccount.UseParentApp' },
    });
    return this.http
      .post<void>('/api/v1/auth/logout', null, { headers: ajaxHeaders(), context: skipAuth() })
      .pipe(
        catchError(() => of(undefined)),
        switchMap(() => throwError(() => refused)),
      );
  }

  private endSession(): void {
    this.sessionEpoch++;
    this.refreshInFlight = null;
    this.clear();
  }

  private clear(): void {
    this.accessToken.set(null);
    this.currentUser.set(null);
  }
}

/**
 * Runs `source` while holding the cross-tab refresh lock; emits null without running it when
 * `stale()` says the session ended while waiting. Without the Web Locks API it runs directly.
 */
function underRefreshLock<T>(source: Observable<T>, stale: () => boolean): Observable<T | null> {
  const locks = typeof navigator === 'undefined' ? undefined : navigator.locks;
  if (!locks) {
    return source;
  }
  return new Observable<T | null>((subscriber) => {
    const abort = new AbortController();
    let inner: Subscription | undefined;
    locks
      .request(REFRESH_LOCK, { mode: 'exclusive', signal: abort.signal }, () => {
        if (subscriber.closed) {
          return undefined;
        }
        if (stale()) {
          subscriber.next(null);
          subscriber.complete();
          return undefined;
        }
        // The lock is held until the call settles or is unsubscribed.
        return new Promise<void>((release) => {
          inner = source.pipe(finalize(release)).subscribe(subscriber);
        });
      })
      .catch((error: unknown) => {
        if (!abort.signal.aborted) {
          subscriber.error(error);
        }
      });
    return () => {
      abort.abort();
      inner?.unsubscribe();
    };
  });
}

/**
 * Whether the JWT's `sub` claim is `userId`. The payload is only decoded, not verified: this is a client-side
 * consistency check. A token that cannot be decoded does not match (fail closed).
 */
export function sameSubject(token: string, userId: string): boolean {
  try {
    const payload = token.split('.')[1];
    if (!payload) {
      return false;
    }
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64.padEnd(Math.ceil(base64.length / 4) * 4, '=');
    const sub = (JSON.parse(atob(padded)) as { sub?: unknown }).sub;
    return typeof sub === 'string' && sub.toLowerCase() === userId.toLowerCase();
  } catch {
    return false;
  }
}

/** Only an explicit rejection of the refresh cookie ends the session. */
function endsSession(error: unknown): boolean {
  return error instanceof HttpErrorResponse && (error.status === 401 || error.status === 403);
}

/**
 * Failures worth one more startup attempt: network, conflict and server errors (DEC-447). A rate limit (429) is
 * not retried: a fixed short pause would most likely hit the same limit.
 */
function isTransient(error: unknown): boolean {
  return (
    error instanceof HttpErrorResponse &&
    (error.status === 0 || error.status === 409 || error.status >= 500)
  );
}

function skipAuth(): HttpContext {
  return new HttpContext().set(SKIP_AUTH, true);
}

function ajaxHeaders(): HttpHeaders {
  return new HttpHeaders({ 'X-Requested-With': 'XMLHttpRequest' });
}
