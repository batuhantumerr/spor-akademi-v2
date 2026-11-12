import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { AuthService, CurrentUser, REFRESH_LOCK, RESTORE_RETRY_DELAY_MS } from './auth.service';

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

  describe('parent accounts (P3-01)', () => {
    it('refuses a parent at login: the session ends, the cookie is dropped and the error says why', () => {
      let code: string | undefined;
      auth.login('veli@example.test', 'Veli-Sifresi-2027').subscribe({
        error: (err: { error?: { code?: string } }) => (code = err.error?.code),
      });
      backend
        .expectOne('/api/v1/auth/login')
        .flush({ accessToken: 'parent-token', expiresIn: 900 });
      backend.expectOne('/api/v1/me').flush({ ...user, role: 'Parent' });
      backend.expectOne('/api/v1/auth/logout').flush(null);

      expect(code).toBe('ParentAccount.UseParentApp');
      expect(auth.token()).toBeNull();
      expect(auth.isAuthenticated()).toBe(false);
    });

    it('does not restore a parent session from the refresh cookie', () => {
      let restored: boolean | undefined;
      auth.restore().subscribe((r) => (restored = r));
      backend
        .expectOne('/api/v1/auth/refresh')
        .flush({ accessToken: 'parent-token', expiresIn: 900 });
      backend.expectOne('/api/v1/me').flush({ ...user, role: 'Parent' });
      backend.expectOne('/api/v1/auth/logout').flush(null);

      expect(restored).toBe(false);
      expect(auth.isAuthenticated()).toBe(false);
    });
  });

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

  describe('startup restore (DEC-447)', () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    function fail(status: number): void {
      const request = backend.expectOne('/api/v1/auth/refresh');
      if (status === 0) {
        request.error(new ProgressEvent('error'));
      } else {
        request.flush(null, { status, statusText: 'Error' });
      }
    }

    it.each([0, 409, 429, 500, 503])('retries once after status %s and restores', (status) => {
      let restored: boolean | undefined;
      auth.restore().subscribe((value) => (restored = value));
      fail(status);

      backend.expectNone('/api/v1/auth/refresh');
      expect(restored).toBeUndefined();
      vi.advanceTimersByTime(RESTORE_RETRY_DELAY_MS);
      backend.expectOne('/api/v1/auth/refresh').flush({ accessToken: 'retried', expiresIn: 900 });
      backend.expectOne('/api/v1/me').flush(user);

      expect(restored).toBe(true);
      expect(auth.token()).toBe('retried');
      expect(auth.isAuthenticated()).toBe(true);
    });

    it('does not retry a rejected cookie', () => {
      let restored: boolean | undefined;
      auth.restore().subscribe((value) => (restored = value));
      fail(401);

      vi.advanceTimersByTime(RESTORE_RETRY_DELAY_MS * 4);
      backend.expectNone('/api/v1/auth/refresh');
      expect(restored).toBe(false);
    });

    it('gives up after one retry', () => {
      let restored: boolean | undefined;
      auth.restore().subscribe((value) => (restored = value));
      fail(503);
      vi.advanceTimersByTime(RESTORE_RETRY_DELAY_MS);
      fail(503);

      vi.advanceTimersByTime(RESTORE_RETRY_DELAY_MS * 4);
      backend.expectNone('/api/v1/auth/refresh');
      expect(restored).toBe(false);
      expect(auth.isAuthenticated()).toBe(false);
    });

    it('does not retry after logout', () => {
      let restored: boolean | undefined;
      auth.restore().subscribe((value) => (restored = value));
      fail(503);
      auth.logout().subscribe();
      backend.expectOne('/api/v1/auth/logout').flush(null);

      vi.advanceTimersByTime(RESTORE_RETRY_DELAY_MS * 4);
      backend.expectNone('/api/v1/auth/refresh');
      expect(restored).toBe(false);
    });

    it('starts signed out after the wait without cancelling the refresh call', async () => {
      const boot = auth.restoreAtBoot(5000);
      const refresh = backend.expectOne('/api/v1/auth/refresh');

      vi.advanceTimersByTime(5000);

      expect(await boot).toBe(false);
      expect(refresh.cancelled).toBe(false);
    });

    it('takes the user from the login page to the return URL when the late restore succeeds', async () => {
      vi.useRealTimers();
      const router = TestBed.inject(Router);
      await router.navigateByUrl('/login?returnUrl=%2Fstudents');
      const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
      vi.useFakeTimers();

      const boot = auth.restoreAtBoot(5000);
      const refresh = backend.expectOne('/api/v1/auth/refresh');
      vi.advanceTimersByTime(5000);
      expect(await boot).toBe(false);
      refresh.flush({ accessToken: 'late', expiresIn: 900 });
      backend.expectOne('/api/v1/me').flush(user);

      expect(auth.isAuthenticated()).toBe(true);
      expect(navigate).toHaveBeenCalledWith('/students');
    });

    it('resolves with the restore result when it arrives in time', async () => {
      const boot = auth.restoreAtBoot(5000);
      backend.expectOne('/api/v1/auth/refresh').flush({ accessToken: 'fast', expiresIn: 900 });
      backend.expectOne('/api/v1/me').flush(user);

      expect(await boot).toBe(true);
    });
  });

  describe('cross-tab refresh lock (P3-08)', () => {
    /** Grants one exclusive lock at a time, in request order, like navigator.locks. */
    class FakeLocks {
      readonly names: string[] = [];
      private tail: Promise<unknown> = Promise.resolve();

      request(name: string, _options: LockOptions, callback: () => unknown): Promise<unknown> {
        this.names.push(name);
        const run = this.tail.then(() => callback());
        this.tail = run.catch(() => undefined);
        return run;
      }
    }

    let locks: FakeLocks;
    const flushMicrotasks = () => new Promise((resolve) => setTimeout(resolve));

    beforeEach(() => {
      locks = new FakeLocks();
      Object.defineProperty(navigator, 'locks', { configurable: true, value: locks });
    });

    afterEach(() => {
      delete (navigator as unknown as Record<string, unknown>)['locks'];
    });

    /** A second tab: its own AuthService on the same origin (same lock manager). */
    function otherTab(): AuthService {
      return TestBed.runInInjectionContext(() => new AuthService());
    }

    it('refreshes under the exclusive lock', async () => {
      let result: string | null | undefined;
      auth.refresh().subscribe((token) => (result = token));
      expect(locks.names).toEqual([REFRESH_LOCK]);
      await flushMicrotasks();
      backend.expectOne('/api/v1/auth/refresh').flush({ accessToken: 'locked', expiresIn: 900 });
      expect(result).toBe('locked');
      expect(auth.token()).toBe('locked');
    });

    it('serializes refreshes of two tabs', async () => {
      const second = otherTab();
      const tokens: (string | null)[] = [];
      auth.refresh().subscribe((t) => tokens.push(t));
      second.refresh().subscribe((t) => tokens.push(t));
      await flushMicrotasks();

      // The second tab waits for the lock while the first call is open.
      const first = backend.match('/api/v1/auth/refresh');
      expect(first.length).toBe(1);
      first[0].flush({ accessToken: 'tab-1', expiresIn: 900 });
      await flushMicrotasks();

      backend.expectOne('/api/v1/auth/refresh').flush({ accessToken: 'tab-2', expiresIn: 900 });
      expect(tokens).toEqual(['tab-1', 'tab-2']);
      expect(second.token()).toBe('tab-2');
    });

    it('skips the call when the session ended while waiting for the lock', async () => {
      const second = otherTab();
      auth.refresh().subscribe();
      let result: string | null | undefined;
      second.refresh().subscribe((t) => (result = t));
      await flushMicrotasks();
      const first = backend.expectOne('/api/v1/auth/refresh');

      second.forgetSession();
      first.flush({ accessToken: 'tab-1', expiresIn: 900 });
      await flushMicrotasks();

      backend.expectNone('/api/v1/auth/refresh');
      expect(result).toBeNull();
      expect(second.token()).toBeNull();
    });

    it('still ends the session when the cookie is rejected under the lock', async () => {
      let result: string | null | undefined;
      auth.refresh().subscribe((t) => (result = t));
      await flushMicrotasks();
      backend
        .expectOne('/api/v1/auth/refresh')
        .flush(null, { status: 401, statusText: 'Unauthorized' });
      expect(result).toBeNull();
      expect(auth.token()).toBeNull();
    });

    it('rethrows network errors under the lock without ending the session', async () => {
      let failed = false;
      auth.refresh().subscribe({ error: () => (failed = true) });
      await flushMicrotasks();
      backend.expectOne('/api/v1/auth/refresh').flush(null, { status: 0, statusText: '' });
      expect(failed).toBe(true);
    });

    it('runs the call directly when the Web Locks API is missing', () => {
      Object.defineProperty(navigator, 'locks', { configurable: true, value: undefined });
      auth.refresh().subscribe();
      // Issued synchronously on subscribe, without a lock.
      backend.expectOne('/api/v1/auth/refresh').flush({ accessToken: 'direct', expiresIn: 900 });
      expect(locks.names).toEqual([]);
      expect(auth.token()).toBe('direct');
    });
  });
});
