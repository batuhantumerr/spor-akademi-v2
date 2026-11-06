import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AuthService, CurrentUser } from './auth.service';

const user: CurrentUser = {
  id: 'u1',
  email: 'admin@example.test',
  firstName: 'Ayşe',
  lastName: 'Yılmaz',
  role: 'SchoolAdmin',
  tenantId: 't1',
  mustChangePassword: false,
};

describe('AuthService', () => {
  let auth: AuthService;
  let backend: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'login', children: [] }]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    auth = TestBed.inject(AuthService);
    backend = TestBed.inject(HttpTestingController);
  });

  afterEach(() => backend.verify());

  function restoreSession(): void {
    auth.restore().subscribe();
    backend.expectOne('/api/v1/auth/refresh').flush({ accessToken: 'token-1', expiresIn: 900 });
    backend.expectOne('/api/v1/me').flush(user);
  }

  describe('refresh', () => {
    it('ends the session when the refresh cookie is rejected', () => {
      restoreSession();
      let result: string | null | undefined;
      auth.refresh().subscribe((token) => (result = token));
      backend
        .expectOne('/api/v1/auth/refresh')
        .flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(result).toBeNull();
      expect(auth.token()).toBeNull();
      expect(auth.isAuthenticated()).toBe(false);
    });

    it.each([0, 500, 503])('keeps the session on status %s', (status) => {
      restoreSession();
      let failed = false;
      auth.refresh().subscribe({ error: () => (failed = true) });
      const request = backend.expectOne('/api/v1/auth/refresh');
      if (status === 0) {
        request.error(new ProgressEvent('error'));
      } else {
        request.flush(null, { status, statusText: 'Error' });
      }

      expect(failed).toBe(true);
      expect(auth.token()).toBe('token-1');
      expect(auth.isAuthenticated()).toBe(true);
    });

    it('shares one call between concurrent callers', () => {
      const tokens: (string | null)[] = [];
      auth.refresh().subscribe((t) => tokens.push(t));
      auth.refresh().subscribe((t) => tokens.push(t));
      backend.expectOne('/api/v1/auth/refresh').flush({ accessToken: 'shared', expiresIn: 900 });
      expect(tokens).toEqual(['shared', 'shared']);
    });
  });

  describe('logout during an in-flight refresh', () => {
    it('ignores the late refresh result', () => {
      restoreSession();
      let result: string | null | undefined;
      auth.refresh().subscribe((token) => (result = token));
      const refresh = backend.expectOne('/api/v1/auth/refresh');

      auth.logout().subscribe();
      backend.expectOne('/api/v1/auth/logout').flush(null);
      refresh.flush({ accessToken: 'late-token', expiresIn: 900 });

      expect(result).toBeNull();
      expect(auth.token()).toBeNull();
      expect(auth.isAuthenticated()).toBe(false);
    });

    it('does not restore the user when startup restore finishes after logout', () => {
      let restored: boolean | undefined;
      auth.restore().subscribe((value) => (restored = value));
      const refresh = backend.expectOne('/api/v1/auth/refresh');

      auth.logout().subscribe();
      backend.expectOne('/api/v1/auth/logout').flush(null);
      refresh.flush({ accessToken: 'late-token', expiresIn: 900 });

      backend.expectNone('/api/v1/me');
      expect(restored).toBe(false);
      expect(auth.isAuthenticated()).toBe(false);
    });

    it('starts a fresh refresh after logout instead of reusing the stale one', () => {
      auth.refresh().subscribe();
      const stale = backend.expectOne('/api/v1/auth/refresh');
      auth.logout().subscribe();
      backend.expectOne('/api/v1/auth/logout').flush(null);

      auth.refresh().subscribe();
      const requests = backend.match('/api/v1/auth/refresh');
      expect(requests.length).toBe(1);
      stale.flush({ accessToken: 'late-token', expiresIn: 900 });
      requests[0].flush({ accessToken: 'fresh-token', expiresIn: 900 });
      expect(auth.token()).toBe('fresh-token');
    });
  });
});
