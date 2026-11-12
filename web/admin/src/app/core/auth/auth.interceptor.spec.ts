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
