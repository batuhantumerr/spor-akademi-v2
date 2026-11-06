import { PageResult, PagedList } from './paged-list';

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

const page = (items: string[], totalPages: number): PageResult<string> => ({
  items,
  totalPages,
  totalCount: totalPages * 10,
});

describe('PagedList', () => {
  it('ignores a response that arrives after a newer request', async () => {
    const calls: Deferred<PageResult<string>>[] = [];
    const list = new PagedList<string>(() => {
      const d = deferred<PageResult<string>>();
      calls.push(d);
      return d.promise;
    });

    const first = list.load();
    const second = list.goTo(2);
    calls[1].resolve(page(['new'], 3));
    await second;
    calls[0].resolve(page(['old'], 3));
    await first;

    expect(list.items()).toEqual(['new']);
    expect(list.refreshing()).toBe(false);
  });

  it('moves to the last page when the current page no longer exists', async () => {
    const requested: number[] = [];
    const list = new PagedList<string>(async (p) => {
      requested.push(p);
      return p > 2 ? page([], 2) : page([`item-${p}`], 2);
    });

    await list.goTo(3);

    expect(requested).toEqual([3, 2]);
    expect(list.page()).toBe(2);
    expect(list.items()).toEqual(['item-2']);
  });

  it('falls back to page 1 when everything was removed', async () => {
    const list = new PagedList<string>(async () => page([], 0));
    await list.goTo(4);
    expect(list.page()).toBe(1);
    expect(list.isEmpty()).toBe(true);
  });

  it('keeps items and reports a Turkish message when a reload fails', async () => {
    let fail = false;
    const list = new PagedList<string>(async () => {
      if (fail) {
        throw new Error('boom');
      }
      return page(['a'], 1);
    });
    await list.load();
    fail = true;
    await list.load();

    expect(list.items()).toEqual(['a']);
    expect(list.error()).toContain('Beklenmeyen');
    expect(list.isEmpty()).toBe(false);
  });
});
