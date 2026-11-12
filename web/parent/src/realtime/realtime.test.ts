import { QueryClient, type QueryKey } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Session } from '../auth/session';
import { parentKeys } from '../parent/queries';
import { child, fakeApi, json, parentUser, problem, sessionResponse } from '../test/fake-api';
import { expiresSoon, HUB_PATH, Realtime, RECONNECT_DELAYS_MS } from './realtime';
import { ToastStore } from './toasts';

/** A stand-in for @microsoft/signalr's HubConnection: records what the app registers and lets tests drive it. */
const hub = vi.hoisted(() => {
  type Callback = (...args: unknown[]) => void;
  interface Options {
    accessTokenFactory: () => Promise<string>;
  }
  interface RetryPolicy {
    nextRetryDelayInMilliseconds(context: { previousRetryCount: number }): number | null;
  }

  class FakeConnection {
    readonly handlers = new Map<string, Callback>();
    readonly closeCallbacks: Callback[] = [];
    readonly reconnectedCallbacks: Callback[] = [];
    starts = 0;
    stops = 0;
    invoked = 0;

    constructor(
      readonly url: string,
      readonly options: Options,
      readonly retryPolicy: RetryPolicy,
      readonly logLevel: unknown,
      private readonly startOutcome: 'ok' | 'fail',
    ) {}

    start(): Promise<void> {
      this.starts++;
      // Like the real client, the token is asked for on every start.
      return this.options
        .accessTokenFactory()
        .then(() => (this.startOutcome === 'ok' ? undefined : Promise.reject(new Error('negotiate failed'))));
    }

    stop(): Promise<void> {
      this.stops++;
      this.close();
      return Promise.resolve();
    }

    invoke(): Promise<void> {
      this.invoked++;
      return Promise.resolve();
    }

    on(name: string, callback: Callback): void {
      this.handlers.set(name, callback);
    }

    onclose(callback: Callback): void {
      this.closeCallbacks.push(callback);
    }

    onreconnected(callback: Callback): void {
      this.reconnectedCallbacks.push(callback);
    }

    onreconnecting(): void {
      // Not used by the app.
    }

    // Test drivers.
    emit(name: string, payload: unknown): void {
      this.handlers.get(name)?.(payload);
    }

    close(): void {
      for (const callback of this.closeCallbacks) callback();
    }

    reconnected(): void {
      for (const callback of this.reconnectedCallbacks) callback('connection-id');
    }
  }

  const state = {
    built: [] as FakeConnection[],
    startOutcomes: [] as ('ok' | 'fail')[],
  };

  class HubConnectionBuilder {
    private url = '';
    private options: Options | null = null;
    private retryPolicy: RetryPolicy | null = null;
    private logLevel: unknown = undefined;

    withUrl(url: string, options: Options) {
      this.url = url;
      this.options = options;
      return this;
    }

    withAutomaticReconnect(retryPolicy: RetryPolicy) {
      this.retryPolicy = retryPolicy;
      return this;
    }

    configureLogging(level: unknown) {
      this.logLevel = level;
      return this;
    }

    build() {
      if (!this.options || !this.retryPolicy) throw new Error('incomplete builder');
      const connection = new FakeConnection(
        this.url,
        this.options,
        this.retryPolicy,
        this.logLevel,
        state.startOutcomes.shift() ?? 'ok',
      );
      state.built.push(connection);
      return connection;
    }
  }

  return { state, HubConnectionBuilder, FakeConnection };
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: hub.HubConnectionBuilder,
  LogLevel: { None: 6 },
}));

const ORIGIN = 'https://veli.example.test';
/** 2026-11-12 12:00 in Istanbul. */
const NOW = Date.UTC(2026, 10, 12, 9, 0);
const OTHER_CHILD = '0190a1b2-0000-7000-8000-0000000000c2';

function jwt(expiresAtMs: number): string {
  const payload = btoa(JSON.stringify({ exp: Math.floor(expiresAtMs / 1000), sub: 'synthetic' }))
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');
  return `header.${payload}.signature`;
}

const VALID = jwt(NOW + 15 * 60_000);
const EXPIRED = jwt(NOW - 60_000);

let session: Session;
let queryClient: QueryClient;
let toasts: ToastStore;
let realtime: Realtime;
let stop: (() => void) | null;

function startRealtime(): void {
  realtime = new Realtime({
    session,
    queryClient,
    toasts,
    origin: ORIGIN,
    now: () => NOW,
    random: () => 0.5,
  });
  stop = realtime.start();
}

const built = () => hub.state.built;
const last = () => {
  const connection = hub.state.built.at(-1);
  if (!connection) throw new Error('No connection was built.');
  return connection;
};

/** Lets pending promise callbacks (start, the token factory) run. */
const settle = () => vi.advanceTimersByTimeAsync(0);

function seedCache(): QueryKey[] {
  const keys: QueryKey[] = [
    parentKeys.children(),
    ...[child().id, OTHER_CHILD].flatMap((id) => [
      parentKeys.schedule(id),
      parentKeys.attendance(id),
      parentKeys.dues(id),
      parentKeys.payments(id),
      parentKeys.announcements(id),
    ]),
  ];
  for (const key of keys) queryClient.setQueryData(key, key === keys[0] ? [child()] : {});
  return keys;
}

const invalidated = (key: QueryKey) => queryClient.getQueryState(key)?.isInvalidated ?? false;

function invalidatedKeys(keys: QueryKey[]): string[] {
  return keys.filter(invalidated).map((key) => key.join('/'));
}

function resetInvalidation(keys: QueryKey[]): void {
  for (const key of keys) queryClient.setQueryData(key, queryClient.getQueryData(key));
}

const messages = () => toasts.getState().map((toast) => toast.message);

beforeEach(() => {
  vi.useFakeTimers({ now: NOW });
  hub.state.built.length = 0;
  hub.state.startOutcomes.length = 0;
  session = new Session();
  queryClient = new QueryClient();
  toasts = new ToastStore();
  stop = null;
});

afterEach(() => {
  stop?.();
  toasts.clear();
  vi.useRealTimers();
});

describe('notifications hub connection (P3-08)', () => {
  it('does not connect while signed out and connects once signed in', async () => {
    startRealtime();
    session.forget();
    await settle();
    expect(built()).toHaveLength(0);

    session.adopt(VALID, parentUser());
    await settle();

    expect(built()).toHaveLength(1);
    expect(last().url).toBe(`${ORIGIN}${HUB_PATH}`);
    expect(last().starts).toBe(1);
    expect(last().logLevel).toBe(6); // LogLevel.None: nothing on the console.
  });

  it('connects at start-up when the session was already restored', async () => {
    session.adopt(VALID, parentUser());
    startRealtime();
    await settle();
    expect(built()).toHaveLength(1);
  });

  it('never connects for a session that is not a parent or still has to set a password', async () => {
    startRealtime();
    session.adopt(VALID, parentUser({ mustChangePassword: true }));
    await settle();
    session.adopt(VALID, parentUser({ role: 'SchoolAdmin' }));
    await settle();
    expect(built()).toHaveLength(0);
  });

  it('keeps one connection when a token refresh re-announces the same session', async () => {
    startRealtime();
    session.adopt(VALID, parentUser());
    await settle();
    session.adopt(jwt(NOW + 20 * 60_000), parentUser());
    await settle();
    expect(built()).toHaveLength(1);
    expect(last().stops).toBe(0);
  });

  it('hands the hub the in-memory token while it is valid', async () => {
    const api = fakeApi();
    startRealtime();
    session.adopt(VALID, parentUser());
    await settle();

    await expect(last().options.accessTokenFactory()).resolves.toBe(VALID);
    expect(api.calls).toHaveLength(0);
  });

  it('refreshes an expired token before a (re)connect and hands over the fresh one', async () => {
    const fresh = jwt(NOW + 15 * 60_000 + 1000);
    const api = fakeApi({ 'POST /api/v1/auth/refresh': () => json(sessionResponse(parentUser(), fresh)) });
    startRealtime();
    session.adopt(EXPIRED, parentUser());
    await settle();

    expect(api.callsTo('POST /api/v1/auth/refresh')).toHaveLength(1);
    await expect(last().options.accessTokenFactory()).resolves.toBe(fresh);
    expect(built()).toHaveLength(1);
  });

  it('stops for good when the refresh is rejected: signed out, no retry loop', async () => {
    fakeApi({ 'POST /api/v1/auth/refresh': () => problem(401, 'Auth.SessionExpired') });
    startRealtime();
    session.adopt(EXPIRED, parentUser());
    await settle();

    expect(session.getState().status).toBe('signedOut');
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(built()).toHaveLength(1);
  });

  it('disconnects on logout and clears the toasts', async () => {
    fakeApi({ 'POST /api/v1/auth/logout': () => new Response(null, { status: 204 }) });
    startRealtime();
    session.adopt(VALID, parentUser());
    await settle();
    toasts.show('Yeni duyuru yayımlandı');

    await session.logout();
    await vi.advanceTimersByTimeAsync(10 * 60_000);

    expect(last().stops).toBe(1);
    expect(built()).toHaveLength(1);
    expect(toasts.getState()).toEqual([]);
  });

  it('uses SignalR reconnects at 0, 2, 5, 10 and 30 seconds, and none once signed out', async () => {
    startRealtime();
    session.adopt(VALID, parentUser());
    await settle();
    const policy = last().retryPolicy;

    const delays = [0, 1, 2, 3, 4, 5].map((previousRetryCount) =>
      policy.nextRetryDelayInMilliseconds({ previousRetryCount }),
    );
    expect(delays).toEqual([...RECONNECT_DELAYS_MS, null]);

    session.forget();
    expect(policy.nextRetryDelayInMilliseconds({ previousRetryCount: 0 })).toBeNull();
  });

  it('starts a new connection with backoff after the connection finally closed while signed in', async () => {
    startRealtime();
    session.adopt(VALID, parentUser());
    await settle();

    last().close(); // SignalR gave up, or the server closed it.
    await vi.advanceTimersByTimeAsync(999);
    expect(built()).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(built()).toHaveLength(2);
    expect(last().starts).toBe(1);

    last().close();
    await vi.advanceTimersByTimeAsync(4_999);
    expect(built()).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(built()).toHaveLength(3);
  });

  it('retries a failed start with backoff', async () => {
    hub.state.startOutcomes.push('fail', 'fail');
    startRealtime();
    session.adopt(VALID, parentUser());
    await settle();
    expect(built()).toHaveLength(1);

    await vi.advanceTimersByTimeAsync(1_000);
    expect(built()).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(5_000);
    expect(built()).toHaveLength(3);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(built()).toHaveLength(3); // The third start succeeded.
  });

  it('does not restart after a close once the parent signed out', async () => {
    startRealtime();
    session.adopt(VALID, parentUser());
    await settle();
    const first = last();
    session.forget();
    first.close();
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(built()).toHaveLength(1);
  });

  it('refetches everything a notification could change after connecting and after a reconnect', async () => {
    const keys = seedCache();
    startRealtime();
    session.adopt(VALID, parentUser());
    await settle();

    const expected = keys
      .filter((key) => key[3] !== 'schedule')
      .map((key) => key.join('/'))
      .sort();
    expect(invalidatedKeys(keys).sort()).toEqual(expected);

    resetInvalidation(keys);
    expect(invalidatedKeys(keys)).toEqual([]);
    last().reconnected();
    await settle();
    expect(invalidatedKeys(keys).sort()).toEqual(expected);
  });

  it('never invokes a hub method', async () => {
    seedCache();
    startRealtime();
    session.adopt(VALID, parentUser());
    await settle();
    last().emit('notify', { type: 'payment.recorded', id: 'p1', studentId: child().id, groupId: null });
    expect(last().invoked).toBe(0);
  });
});

describe('notify (P3-08)', () => {
  let keys: QueryKey[];

  async function connected(): Promise<void> {
    keys = seedCache();
    startRealtime();
    session.adopt(VALID, parentUser());
    await settle();
    resetInvalidation(keys);
  }

  async function notify(payload: Record<string, unknown>): Promise<void> {
    last().emit('notify', payload);
    await settle();
  }

  const base = {
    id: '0190a1b2-0000-7000-8000-0000000000f1',
    groupId: null,
    studentId: null,
    date: null,
    status: null,
  };

  it('attendance: refetches that child’s attendance and shows today’s status with the cached first name', async () => {
    await connected();
    await notify({
      ...base,
      type: 'attendance.recorded',
      studentId: child().id,
      date: '2026-11-12',
      status: 'Present',
      // Never trusted for display:
      firstName: 'Sahte',
      message: 'Sahte mesaj',
    });

    expect(invalidatedKeys(keys)).toEqual([parentKeys.attendance(child().id).join('/')]);
    expect(messages()).toEqual(['Ada bugün antrenmana katıldı']);
  });

  it.each([
    ['Present', 'Ada bugün antrenmana katıldı'],
    ['Absent', 'Ada bugün antrenmana katılmadı'],
    ['Late', 'Ada bugün antrenmana geç geldi'],
    ['Excused', 'Ada bugün antrenmanda izinli'],
  ])('attendance %s today', async (status, message) => {
    await connected();
    await notify({ ...base, type: 'attendance.recorded', studentId: child().id, date: '2026-11-12', status });
    expect(messages()).toEqual([message]);
  });

  it.each([
    ['Present', 'Ada, 10 Kasım antrenmanına katıldı'],
    ['Absent', 'Ada, 10 Kasım antrenmanına katılmadı'],
    ['Late', 'Ada, 10 Kasım antrenmanına geç geldi'],
    ['Excused', 'Ada, 10 Kasım antrenmanında izinli'],
  ])('attendance %s on an earlier day names the day', async (status, message) => {
    await connected();
    await notify({ ...base, type: 'attendance.recorded', studentId: child().id, date: '2026-11-10', status });
    expect(messages()).toEqual([message]);
  });

  it('uses the school’s day, not the browser’s, for "bugün"', async () => {
    await connected();
    // 22:30 UTC on 11 November is already 12 November in Istanbul.
    stop?.();
    realtime = new Realtime({
      session,
      queryClient,
      toasts,
      origin: ORIGIN,
      now: () => Date.UTC(2026, 10, 11, 22, 30),
      random: () => 0.5,
    });
    stop = realtime.start();
    await settle();
    await notify({
      ...base,
      type: 'attendance.recorded',
      studentId: child().id,
      date: '2026-11-12',
      status: 'Late',
    });
    expect(messages()).toEqual(['Ada bugün antrenmana geç geldi']);
  });

  it('falls back to "Çocuğunuz" when the child is not in the cache', async () => {
    await connected();
    await notify({
      ...base,
      type: 'attendance.recorded',
      studentId: OTHER_CHILD,
      date: '2026-11-12',
      status: 'Absent',
    });
    expect(messages()).toEqual(['Çocuğunuz bugün antrenmana katılmadı']);
    expect(invalidatedKeys(keys)).toEqual([parentKeys.attendance(OTHER_CHILD).join('/')]);
  });

  it('attendance without a known status still refetches, with a neutral text', async () => {
    await connected();
    await notify({
      ...base,
      type: 'attendance.recorded',
      studentId: child().id,
      date: '2026-11-12',
      status: 'Bogus',
    });
    expect(messages()).toEqual(['Ada için yoklama güncellendi']);
  });

  it('payment: refetches that child’s dues and payments', async () => {
    await connected();
    await notify({
      ...base,
      type: 'payment.recorded',
      studentId: child().id,
      groupId: 'g1',
      amount: '999.00',
    });
    expect(invalidatedKeys(keys).sort()).toEqual(
      [parentKeys.dues(child().id), parentKeys.payments(child().id)].map((key) => key.join('/')).sort(),
    );
    expect(messages()).toEqual(['Ada için ödeme alındı']);
  });

  it('announcement: refetches every child’s announcements', async () => {
    await connected();
    await notify({ ...base, type: 'announcement.published', groupId: 'g1', title: 'Sahte başlık' });
    expect(invalidatedKeys(keys).sort()).toEqual(
      [parentKeys.announcements(child().id), parentKeys.announcements(OTHER_CHILD)]
        .map((key) => key.join('/'))
        .sort(),
    );
    expect(messages()).toEqual(['Yeni duyuru yayımlandı']);
  });

  it.each([
    null,
    'text',
    { type: 'unknown.event', id: 'x' },
    { type: 'payment.recorded' },
    { type: 'payment.recorded', id: 42 },
  ])('ignores a payload it does not understand: %j', async (payload) => {
    await connected();
    last().emit('notify', payload);
    await settle();
    expect(messages()).toEqual([]);
    expect(invalidatedKeys(keys)).toEqual([]);
  });
});

describe('expiresSoon', () => {
  it('reads the JWT exp with a 30-second margin; unreadable tokens are left to the server', () => {
    expect(expiresSoon(jwt(NOW + 60_000), NOW)).toBe(false);
    expect(expiresSoon(jwt(NOW + 20_000), NOW)).toBe(true);
    expect(expiresSoon(jwt(NOW - 1), NOW)).toBe(true);
    expect(expiresSoon('opaque-token', NOW)).toBe(false);
    expect(expiresSoon('a.%%%.c', NOW)).toBe(false);
  });
});
