import {
  HttpClient,
  HttpErrorResponse,
  provideHttpClient,
  withInterceptors,
} from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { setLanguage } from '../i18n/language';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

/** A synthetic, unsigned JWT whose payload carries `sub`; the client only decodes it. */
function jwt(sub: string): string {
  const payload = btoa(JSON.stringify({ sub })).replace(/=+$/, '');
  return `eyJhbGciOiJub25lIn0.${payload}.sig`;
}

describe('authInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let auth: AuthService;
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'login', children: [] },
          { path: 'sports', children: [] },
        ]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);
    router = TestBed.inject(Router);
  });

  afterEach(() => backend.verify());

  /** Puts a token in memory through a successful refresh. */
  function signIn(token: string): void {
    auth.refresh().subscribe();
    backend.expectOne('/api/v1/auth/refresh').flush({ accessToken: token, expiresIn: 900 });
  }

  it('sends the UI language as Accept-Language to the API only (P3-10)', () => {
    http.get('/api/v1/sports').subscribe();
    expect(backend.expectOne('/api/v1/sports').request.headers.get('Accept-Language')).toBe('tr');

    setLanguage('en');
    http.get('/api/v1/sports').subscribe();
    expect(backend.expectOne('/api/v1/sports').request.headers.get('Accept-Language')).toBe('en');

    http.get('/assets/x.json').subscribe();
    expect(backend.expectOne('/assets/x.json').request.headers.has('Accept-Language')).toBe(false);
  });

  it('refreshes once on 401 and retries with the new token', () => {
    let body: unknown;
    http.get('/api/v1/sports').subscribe((value) => (body = value));

    backend.expectOne('/api/v1/sports').flush(null, { status: 401, statusText: 'Unauthorized' });
    const refresh = backend.expectOne('/api/v1/auth/refresh');
    expect(refresh.request.headers.get('X-Requested-With')).toBe('XMLHttpRequest');
    refresh.flush({ accessToken: 'new-token', expiresIn: 900 });

    const retry = backend.expectOne('/api/v1/sports');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer new-token');
    retry.flush({ items: [] });
    expect(body).toEqual({ items: [] });
  });

  it('gives up when the refresh fails', () => {
    let failed = false;
    http.get('/api/v1/sports').subscribe({ error: () => (failed = true) });

    backend.expectOne('/api/v1/sports').flush(null, { status: 401, statusText: 'Unauthorized' });
    backend
      .expectOne('/api/v1/auth/refresh')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(failed).toBe(true);
  });

  it('keeps the current URL as returnUrl when the session expires', async () => {
    await router.navigateByUrl('/sports');
    const navigate = vi.spyOn(router, 'navigate');
    http.get('/api/v1/sports').subscribe({ error: () => undefined });

    backend.expectOne('/api/v1/sports').flush(null, { status: 401, statusText: 'Unauthorized' });
    backend
      .expectOne('/api/v1/auth/refresh')
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(navigate).toHaveBeenCalledWith(['/login'], {
      queryParams: { expired: 1, returnUrl: '/sports' },
    });
  });

  it.each([
    ['a network error', 0],
    ['a server error', 503],
  ])('surfaces %s during refresh without ending the session', (_label, status) => {
    signIn('old-token');
    const navigate = vi.spyOn(router, 'navigate');
    let error: HttpErrorResponse | undefined;
    http.get('/api/v1/sports').subscribe({ error: (e: HttpErrorResponse) => (error = e) });

    backend.expectOne('/api/v1/sports').flush(null, { status: 401, statusText: 'Unauthorized' });
    const refresh = backend.expectOne('/api/v1/auth/refresh');
    if (status === 0) {
      refresh.error(new ProgressEvent('error'));
    } else {
      refresh.flush(null, { status, statusText: 'Unavailable' });
    }

    expect(error?.status).toBe(status);
    expect(auth.token()).toBe('old-token');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('retries with a newer token without refreshing again', () => {
    signIn('old-token');
    let body: unknown;
    http.get('/api/v1/sports').subscribe((value) => (body = value));
    const first = backend.expectOne('/api/v1/sports');
    expect(first.request.headers.get('Authorization')).toBe('Bearer old-token');

    // Another call refreshes while the first request is still pending.
    signIn('new-token');
    first.flush(null, { status: 401, statusText: 'Unauthorized' });

    backend.expectNone('/api/v1/auth/refresh');
    const retry = backend.expectOne('/api/v1/sports');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer new-token');
    retry.flush({ items: [] });
    expect(body).toEqual({ items: [] });
  });

  it('does not redirect when the user logged out during the refresh', () => {
    signIn('old-token');
    http.get('/api/v1/sports').subscribe({ error: () => undefined });
    backend.expectOne('/api/v1/sports').flush(null, { status: 401, statusText: 'Unauthorized' });
    const refresh = backend.expectOne('/api/v1/auth/refresh');

    const navigate = vi.spyOn(router, 'navigate');
    auth.logout().subscribe();
    backend.expectOne('/api/v1/auth/logout').flush(null);
    refresh.flush({ accessToken: 'late-token', expiresIn: 900 });

    expect(auth.token()).toBeNull();
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith(['/login']);
  });

  describe('401 of a request sent under an older session epoch', () => {
    it('neither refreshes nor expires the login that started meanwhile', () => {
      signIn('old-token');
      const navigate = vi.spyOn(router, 'navigate');
      let error: HttpErrorResponse | undefined;
      // E.g. the startup restore's /me, still on the wire while somebody signs in.
      http.get('/api/v1/sports').subscribe({ error: (e: HttpErrorResponse) => (error = e) });
      const stale = backend.expectOne('/api/v1/sports');

      auth.login('b@example.test', 'Sentetik-Sifre-2027').subscribe();
      backend.expectOne('/api/v1/auth/login').flush({ accessToken: jwt('u2'), expiresIn: 900 });
      const me = backend.expectOne('/api/v1/me');
      expect(me.request.headers.get('Authorization')).toBe(`Bearer ${jwt('u2')}`);

      stale.flush(null, { status: 401, statusText: 'Unauthorized' });
      backend.expectNone('/api/v1/auth/refresh');
      backend.expectNone('/api/v1/sports'); // no retry with the new session's token
      expect(error?.status).toBe(401);

      me.flush({
        id: 'u2',
        email: 'b@example.test',
        firstName: 'Deniz',
        lastName: 'Kaya',
        role: 'SchoolAdmin',
        tenantId: 't1',
        mustChangePassword: false,
      });
      expect(auth.user()?.id).toBe('u2');
      expect(auth.token()).toBe(jwt('u2'));
      expect(navigate).not.toHaveBeenCalled();
    });

    it('still refreshes a 401 of the current epoch', () => {
      signIn('old-token');
      http.get('/api/v1/sports').subscribe({ error: () => undefined });
      backend.expectOne('/api/v1/sports').flush(null, { status: 401, statusText: 'Unauthorized' });
      backend.expectOne('/api/v1/auth/refresh').flush({ accessToken: 'new-token', expiresIn: 900 });
      backend.expectOne('/api/v1/sports').flush({ items: [] });
    });
  });

  it('expires the session once and does not retry when the refreshed token belongs to another user', async () => {
    await router.navigateByUrl('/sports');
    auth.login('a@example.test', 'Sentetik-Sifre-2027').subscribe();
    backend.expectOne('/api/v1/auth/login').flush({ accessToken: jwt('u1'), expiresIn: 900 });
    backend.expectOne('/api/v1/me').flush({
      id: 'u1',
      email: 'a@example.test',
      firstName: 'Ayşe',
      lastName: 'Yılmaz',
      role: 'SchoolAdmin',
      tenantId: 't1',
      mustChangePassword: false,
    });
    const navigate = vi.spyOn(router, 'navigate');
    let error: HttpErrorResponse | undefined;
    http.get('/api/v1/sports').subscribe({ error: (e: HttpErrorResponse) => (error = e) });

    backend.expectOne('/api/v1/sports').flush(null, { status: 401, statusText: 'Unauthorized' });
    backend.expectOne('/api/v1/auth/refresh').flush({ accessToken: jwt('u9'), expiresIn: 900 });

    backend.expectNone('/api/v1/sports');
    expect(error?.status).toBe(401);
    expect(auth.token()).toBeNull();
    expect(auth.isAuthenticated()).toBe(false);
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith(['/login'], {
      queryParams: { expired: 1, returnUrl: '/sports' },
    });
  });

  it('does not refresh on coded business 401s', () => {
    let code: string | undefined;
    http.post('/api/v1/auth/change-password', {}).subscribe({
      error: (err: { error: { code: string } }) => (code = err.error.code),
    });

    backend
      .expectOne('/api/v1/auth/change-password')
      .flush({ code: 'Auth.AccountLocked' }, { status: 401, statusText: 'Unauthorized' });

    backend.expectNone('/api/v1/auth/refresh');
    expect(code).toBe('Auth.AccountLocked');
  });

  it('leaves non-API requests alone', () => {
    http.get('/assets/x.json').subscribe();
    expect(backend.expectOne('/assets/x.json').request.headers.has('Authorization')).toBe(false);
  });
});
