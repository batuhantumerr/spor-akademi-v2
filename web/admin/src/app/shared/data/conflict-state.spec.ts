import { ConflictState } from './conflict-state';

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason: unknown) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

describe('ConflictState', () => {
  it('keeps only the latest load', async () => {
    const calls: Deferred<string>[] = [];
    const state = new ConflictState<string>(() => {
      const d = deferred<string>();
      calls.push(d);
      return d.promise;
    });

    const first = state.start();
    const second = state.load();
    calls[1].resolve('v2');
    await second;
    calls[0].resolve('v1');
    await first;

    expect(state.fresh()).toBe('v2');
  });

  it('ignores a response that arrives after the conflict was closed', async () => {
    const call = deferred<string>();
    const state = new ConflictState<string>(() => call.promise);

    const loading = state.start();
    expect(state.close()).toBeNull();
    call.resolve('late');
    await loading;

    expect(state.open()).toBe(false);
    expect(state.fresh()).toBeNull();
  });

  it('ignores a late failure too', async () => {
    const call = deferred<string>();
    const state = new ConflictState<string>(
      () => call.promise,
      () => 'failed',
    );

    const loading = state.start();
    state.close();
    call.reject(new Error('boom'));
    await loading;

    expect(state.error()).toBeNull();
  });

  it('reports a failure of the latest load and recovers on retry', async () => {
    let fail = true;
    const state = new ConflictState<string>(
      async () => {
        if (fail) {
          throw new Error('boom');
        }
        return 'fresh';
      },
      () => 'failed',
    );

    await state.start();
    expect(state.error()).toBe('failed');
    fail = false;
    await state.load();
    expect(state.error()).toBeNull();
    expect(state.close()).toBe('fresh');
  });

  it('does nothing when loading while closed', async () => {
    const fetch = vi.fn(async () => 'x');
    const state = new ConflictState<string>(fetch);
    await state.load();
    expect(fetch).not.toHaveBeenCalled();
  });
});
