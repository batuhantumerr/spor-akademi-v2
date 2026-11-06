import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { authInterceptor } from './auth.interceptor';

describe('authInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'login', children: [] }]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

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

  it('leaves non-API requests alone', () => {
    http.get('/assets/x.json').subscribe();
    expect(backend.expectOne('/assets/x.json').request.headers.has('Authorization')).toBe(false);
  });
});
