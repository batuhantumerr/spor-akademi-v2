import { afterEach, describe, expect, it } from 'vitest';
import { fakeApi, json, parentUser, sessionResponse } from '../test/fake-api';
import { REFRESH_LOCK, Session } from './session';

/** An exclusive Web Lock shared by every Session in the test, as the tabs of one origin share navigator.locks. */
function installLocks() {
  let tail: Promise<unknown> = Promise.resolve();
  const requests: { name: string; mode: string | undefined }[] = [];
  const locks = {
    request(name: string, options: { mode?: string }, callback: () => unknown): Promise<unknown> {
      requests.push({ name, mode: options.mode });
      const run = tail.then(() => callback());
      tail = run.catch(() => undefined);
      return run;
    },
  };
  Object.defineProperty(navigator, 'locks', { configurable: true, value: locks });
  return { requests, locks };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

const flush = async () => {
  for (let i = 0; i < 10; i++) await Promise.resolve();
};

afterEach(() => {
  Reflect.deleteProperty(navigator, 'locks');
});

describe('refresh across tabs (P3-08)', () => {
  it('takes the exclusive sa-refresh lock around the refresh call', async () => {
    const { requests } = installLocks();
    fakeApi({ 'POST /api/v1/auth/refresh': () => json(sessionResponse(parentUser(), 'access-2')) });
    const session = new Session();

    await expect(session.refresh()).resolves.toBe('access-2');
    expect(requests).toEqual([{ name: 'sa-refresh', mode: 'exclusive' }]);
    expect(REFRESH_LOCK).toBe('sa-refresh');
  });

  it('serializes the refreshes of two tabs: the second waits until the first has rotated the cookie', async () => {
    installLocks();
    const first = deferred<Response>();
    let calls = 0;
    const api = fakeApi({
      'POST /api/v1/auth/refresh': () => {
        calls++;
        return calls === 1 ? first.promise : json(sessionResponse(parentUser(), 'access-3'));
      },
    });
    const tabA = new Session();
    const tabB = new Session();

    const a = tabA.refresh();
    const b = tabB.refresh();
    await flush();
    expect(api.calls).toHaveLength(1);

    first.resolve(json(sessionResponse(parentUser(), 'access-2')));
    await expect(a).resolves.toBe('access-2');
    await expect(b).resolves.toBe('access-3');
    expect(api.calls).toHaveLength(2);
  });

  it('skips the call when this tab got a new token while waiting for the lock', async () => {
    const { locks } = installLocks();
    const api = fakeApi();
    const session = new Session();
    session.adopt('access-1', parentUser());

    const gate = deferred<undefined>();
    void locks.request(REFRESH_LOCK, { mode: 'exclusive' }, () => gate.promise); // Another tab is refreshing.
    const queued = session.refresh();
    session.adopt('access-9', parentUser());
    gate.resolve(undefined);

    await expect(queued).resolves.toBe('access-9');
    expect(api.calls).toHaveLength(0);
  });

  it('skips the call when the session ended while waiting for the lock', async () => {
    const { locks } = installLocks();
    const api = fakeApi();
    const session = new Session();
    session.adopt('access-1', parentUser());

    const gate = deferred<undefined>();
    void locks.request(REFRESH_LOCK, { mode: 'exclusive' }, () => gate.promise);
    const queued = session.refresh();
    session.forget();
    gate.resolve(undefined);

    await expect(queued).resolves.toBeNull();
    expect(api.calls).toHaveLength(0);
    expect(session.getState().status).toBe('signedOut');
  });

  it('refreshes directly when the Web Locks API is not available', async () => {
    Object.defineProperty(navigator, 'locks', { configurable: true, value: undefined });
    const api = fakeApi({
      'POST /api/v1/auth/refresh': () => json(sessionResponse(parentUser(), 'access-2')),
    });
    const session = new Session();

    await expect(session.refresh()).resolves.toBe('access-2');
    expect(api.calls).toHaveLength(1);
  });
});
