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
  catchError,
  defer,
  finalize,
  map,
  of,
  shareReplay,
  switchMap,
  tap,
  throwError,
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

  readonly user = this.currentUser.asReadonly();
  readonly isAuthenticated = computed(() => this.currentUser() !== null);
  readonly role = computed(() => this.currentUser()?.role ?? null);
  readonly mustChangePassword = computed(() => this.currentUser()?.mustChangePassword ?? false);

  token(): string | null {
    return this.accessToken();
  }

  /** Identifies the current session; changes on logout and expiry. */
  epoch(): number {
    return this.sessionEpoch;
  }

  login(email: string, password: string): Observable<CurrentUser> {
    return this.http
      .post<TokenResponse>('/api/v1/auth/login', { email, password }, { context: skipAuth() })
      .pipe(
        tap((response) => this.accessToken.set(response.accessToken)),
        switchMap(() => this.loadMe(this.sessionEpoch)),
      );
  }

  /** Restores the session at startup; resolves to false when there is no valid refresh cookie. */
  restore(): Observable<boolean> {
    return defer(() => {
      const epoch = this.sessionEpoch;
      return this.refresh().pipe(
        switchMap((token) => (token ? this.loadMe(epoch).pipe(map(() => true)) : of(false))),
        catchError(() => of(false)),
      );
    });
  }

  /**
   * One refresh at a time; concurrent 401s wait for the same call. Emits the new token, or null
   * when the refresh cookie was rejected (401/403: the session is over). Network and server errors
   * are re-thrown without touching the session, so a flaky connection does not log the user out.
   */
  refresh(): Observable<string | null> {
    if (this.refreshInFlight) {
      return this.refreshInFlight;
    }
    const epoch = this.sessionEpoch;
    const call: Observable<string | null> = this.http
      .post<TokenResponse>('/api/v1/auth/refresh', null, {
        headers: ajaxHeaders(),
        context: skipAuth(),
      })
      .pipe(
        map((response) => {
          if (epoch !== this.sessionEpoch) {
            return null; // Logged out while the refresh was in flight.
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
      switchMap((user) => (user.role === 'Parent' ? this.refuseParent() : of(user))),
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

/** Only an explicit rejection of the refresh cookie ends the session. */
function endsSession(error: unknown): boolean {
  return error instanceof HttpErrorResponse && (error.status === 401 || error.status === 403);
}

function skipAuth(): HttpContext {
  return new HttpContext().set(SKIP_AUTH, true);
}

function ajaxHeaders(): HttpHeaders {
  return new HttpHeaders({ 'X-Requested-With': 'XMLHttpRequest' });
}
