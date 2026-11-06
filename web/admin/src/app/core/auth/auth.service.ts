import { HttpClient, HttpContext, HttpContextToken, HttpHeaders } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import {
  Observable,
  catchError,
  finalize,
  map,
  of,
  shareReplay,
  switchMap,
  tap,
  throwError,
} from 'rxjs';

export type Role = 'PlatformAdmin' | 'SchoolAdmin' | 'Coach';

export interface CurrentUser {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  role: Role;
  tenantId: string | null;
  mustChangePassword: boolean;
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

  readonly user = this.currentUser.asReadonly();
  readonly isAuthenticated = computed(() => this.currentUser() !== null);
  readonly role = computed(() => this.currentUser()?.role ?? null);
  readonly mustChangePassword = computed(() => this.currentUser()?.mustChangePassword ?? false);

  token(): string | null {
    return this.accessToken();
  }

  login(email: string, password: string): Observable<CurrentUser> {
    return this.http
      .post<TokenResponse>('/api/v1/auth/login', { email, password }, { context: skipAuth() })
      .pipe(
        tap((response) => this.accessToken.set(response.accessToken)),
        switchMap(() => this.loadMe()),
      );
  }

  /** Restores the session at startup; resolves to false when there is no valid refresh cookie. */
  restore(): Observable<boolean> {
    return this.refresh().pipe(
      switchMap((token) => (token ? this.loadMe().pipe(map(() => true)) : of(false))),
      catchError(() => of(false)),
    );
  }

  /** One refresh at a time; concurrent 401s wait for the same call. */
  refresh(): Observable<string | null> {
    this.refreshInFlight ??= this.http
      .post<TokenResponse>('/api/v1/auth/refresh', null, {
        headers: ajaxHeaders(),
        context: skipAuth(),
      })
      .pipe(
        map((response) => response.accessToken),
        tap((token) => this.accessToken.set(token)),
        catchError(() => {
          this.clear();
          return of(null);
        }),
        finalize(() => (this.refreshInFlight = null)),
        shareReplay(1),
      );
    return this.refreshInFlight;
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
        switchMap(() => this.loadMe()),
      );
  }

  logout(): Observable<void> {
    return this.http
      .post<void>('/api/v1/auth/logout', null, { headers: ajaxHeaders(), context: skipAuth() })
      .pipe(
        catchError(() => of(undefined)),
        map(() => undefined),
        finalize(() => {
          this.clear();
          void this.router.navigate(['/login']);
        }),
      );
  }

  /** Clears local state after an unrecoverable 401. */
  expireSession(): void {
    this.clear();
    void this.router.navigate(['/login'], { queryParams: { expired: 1 } });
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

  private loadMe(): Observable<CurrentUser> {
    return this.http.get<CurrentUser>('/api/v1/me').pipe(
      tap((user) => this.currentUser.set(user)),
      catchError((error: unknown) => {
        this.clear();
        return throwError(() => error);
      }),
    );
  }

  private clear(): void {
    this.accessToken.set(null);
    this.currentUser.set(null);
  }
}

function skipAuth(): HttpContext {
  return new HttpContext().set(SKIP_AUTH, true);
}

function ajaxHeaders(): HttpHeaders {
  return new HttpHeaders({ 'X-Requested-With': 'XMLHttpRequest' });
}
