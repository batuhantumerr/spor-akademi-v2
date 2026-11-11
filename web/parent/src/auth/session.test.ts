import { describe, expect, it } from 'vitest';
import { ApiError } from '../api/errors';
import { fakeApi, json, parentUser, problem, sessionResponse } from '../test/fake-api';
import { Session } from './session';

describe('session', () => {
  it('signs a parent in and keeps the token in memory only', async () => {
    const api = fakeApi({ 'POST /api/v1/auth/login': () => json(sessionResponse()) });
    const session = new Session();

    await session.login('veli@example.test', 'uzun-bir-sifre-123');

    expect(session.getState()).toMatchObject({ status: 'signedIn', user: { role: 'Parent' } });
    expect(session.token()).toBe('access-1');
    expect(localStorage.length).toBe(0);
    const body = (await api.calls[0]!.json()) as unknown;
    expect(body).toEqual({ email: 'veli@example.test', password: 'uzun-bir-sifre-123' });
  });

  it.each([
    ['SchoolAdmin', false, 'ParentApp.NotParent'],
    ['Coach', false, 'ParentApp.NotParent'],
    ['Parent', true, 'ParentApp.PasswordSetupRequired'],
  ] as const)(
    'refuses a %s session (must change password: %s) and drops its cookie',
    async (role, mustChange, code) => {
      const api = fakeApi({
        'POST /api/v1/auth/login': () =>
          json(sessionResponse(parentUser({ role, mustChangePassword: mustChange }))),
        'POST /api/v1/auth/logout': () => new Response(null, { status: 204 }),
      });
      const session = new Session();

      await expect(session.login('kisi@example.test', 'uzun-bir-sifre-123')).rejects.toMatchObject({ code });

      expect(session.token()).toBeNull();
      expect(session.getState().status).not.toBe('signedIn');
      const logout = api.callsTo('POST /api/v1/auth/logout');
      expect(logout).toHaveLength(1);
      expect(logout[0]!.headers.get('X-Requested-With')).toBe('XMLHttpRequest');
    },
  );

  it('passes the API error of a failed login on', async () => {
    fakeApi({ 'POST /api/v1/auth/login': () => problem(401, 'Auth.InvalidCredentials') });

    await expect(new Session().login('veli@example.test', 'yanlis')).rejects.toEqual(
      new ApiError('Auth.InvalidCredentials', 401),
    );
  });

  it('restores a session with one refresh call carrying X-Requested-With', async () => {
    const api = fakeApi({
      'POST /api/v1/auth/refresh': () => json(sessionResponse(parentUser(), 'access-2')),
    });
    const session = new Session();

    await session.restore();

    expect(session.getState().status).toBe('signedIn');
    expect(session.token()).toBe('access-2');
    expect(api.calls[0]!.headers.get('X-Requested-With')).toBe('XMLHttpRequest');
    expect(api.calls[0]!.credentials).toBe('same-origin');
  });

  it('ends up signed out, not expired, when there is no refresh cookie', async () => {
    fakeApi({ 'POST /api/v1/auth/refresh': () => problem(401, 'Auth.Unauthenticated') });
    const session = new Session();

    await session.restore();

    expect(session.getState()).toEqual({ status: 'signedOut', user: null, expired: false });
  });

  it('ends up offline, not signed out, when the network is down at start-up (P3-07)', async () => {
    const api = fakeApi({ 'POST /api/v1/auth/refresh': () => Promise.reject(new TypeError('offline')) });
    const session = new Session();

    await session.restore();

    expect(session.getState()).toEqual({ status: 'offline', user: null, expired: false });

    // Back online: one more try restores the session; a second retry does nothing.
    api.on('POST /api/v1/auth/refresh', () => json(sessionResponse(parentUser(), 'access-9')));
    const retry = session.retryRestore();
    expect(session.getState().status).toBe('loading');
    await retry;
    await session.retryRestore();

    expect(session.getState().status).toBe('signedIn');
    expect(session.token()).toBe('access-9');
    expect(api.callsTo('POST /api/v1/auth/refresh')).toHaveLength(2);
  });

  it.each([
    ['no user', () => json({ accessToken: 'access-1', expiresIn: 900 })],
    ['a null user', () => json({ accessToken: 'access-1', expiresIn: 900, user: null })],
    ['no token', () => json({ expiresIn: 900, user: parentUser() })],
    [
      'not JSON',
      () => new Response('<html></html>', { status: 200, headers: { 'Content-Type': 'text/html' } }),
    ],
    ['JSON null', () => json(null)],
  ])('ends up signed out, not offline, when the refresh answer has %s', async (_case, handler) => {
    fakeApi({ 'POST /api/v1/auth/refresh': handler });
    const session = new Session();

    await session.restore();

    expect(session.getState()).toEqual({ status: 'signedOut', user: null, expired: false });
    expect(session.token()).toBeNull();
  });

  it('ends up signed out, not offline, when something other than the send throws a TypeError', async () => {
    // A 200 whose body cannot be read as a session must not be mistaken for a network failure.
    fakeApi({
      'POST /api/v1/auth/refresh': () =>
        ({
          ok: true,
          status: 200,
          json: () => Promise.reject(new TypeError('body stream already read')),
        }) as unknown as Response,
    });
    const session = new Session();

    await session.restore();

    expect(session.getState().status).toBe('signedOut');
  });

  it('ends up signed out when the server fails at start-up', async () => {
    fakeApi({ 'POST /api/v1/auth/refresh': () => problem(503, 'Unexpected.Error') });
    const session = new Session();

    await session.restore();

    expect(session.getState().status).toBe('signedOut');
  });

  it('shares one refresh between concurrent callers', async () => {
    const api = fakeApi({
      'POST /api/v1/auth/refresh': () => json(sessionResponse(parentUser(), 'access-3')),
    });
    const session = new Session();

    const tokens = await Promise.all([session.refresh(), session.refresh(), session.refresh()]);

    expect(tokens).toEqual(['access-3', 'access-3', 'access-3']);
    expect(api.calls).toHaveLength(1);
  });

  it('marks the session expired when the refresh cookie is rejected while signed in', async () => {
    fakeApi({ 'POST /api/v1/auth/refresh': () => problem(401, 'Auth.SessionExpired') });
    const session = new Session();
    session.adopt('access-1', parentUser());
    let ended = 0;
    session.onEnd(() => ended++);

    expect(await session.refresh()).toBeNull();

    expect(session.getState()).toEqual({ status: 'signedOut', user: null, expired: true });
    expect(ended).toBe(1);
  });

  it('keeps the session when a refresh fails for a network reason', async () => {
    fakeApi({ 'POST /api/v1/auth/refresh': () => Promise.reject(new TypeError('offline')) });
    const session = new Session();
    session.adopt('access-1', parentUser());

    await expect(session.refresh()).rejects.toMatchObject({ code: 'Network.Unavailable' });

    expect(session.getState().status).toBe('signedIn');
  });

  it('does not let a refresh that was on the wire revive a logged-out session', async () => {
    let answer: (response: Response) => void = () => undefined;
    fakeApi({
      'POST /api/v1/auth/refresh': () => new Promise<Response>((resolve) => (answer = resolve)),
      'POST /api/v1/auth/logout': () => new Response(null, { status: 204 }),
    });
    const session = new Session();
    session.adopt('access-1', parentUser());

    const refreshing = session.refresh();
    await session.logout();
    answer(json(sessionResponse(parentUser(), 'access-late')));

    expect(await refreshing).toBeNull();
    expect(session.token()).toBeNull();
    expect(session.getState()).toEqual({ status: 'signedOut', user: null, expired: false });
  });
});
