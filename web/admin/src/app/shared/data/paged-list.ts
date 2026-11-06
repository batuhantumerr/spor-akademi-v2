import { computed, signal } from '@angular/core';

import { problemMessage, toProblem } from '../../core/api/problem';

/** Shape of the API's offset-paged responses (appendix D §1.6). */
export interface PageResult<T> {
  items: T[];
  totalCount: number | string;
  totalPages: number | string;
}

/**
 * State for an offset-paged list: items, page, totals, first-load and refresh flags, and a Turkish
 * error message. Only the latest request may update the state, so a slow response never overwrites
 * a newer one; a page past the end (after an archive or delete) moves to the last page and reloads.
 */
export class PagedList<T> {
  readonly items = signal<T[]>([]);
  readonly page = signal(1);
  readonly totalPages = signal(0);
  readonly totalCount = signal(0);
  /** No data yet: the first request is in flight. */
  readonly loading = signal(true);
  /** A request is in flight while data is shown; the list stays in place (aria-busy). */
  readonly refreshing = signal(false);
  readonly error = signal<string | null>(null);
  readonly isEmpty = computed(() => !this.loading() && !this.error() && this.items().length === 0);

  private sequence = 0;

  constructor(private readonly fetch: (page: number) => Promise<PageResult<T>>) {}

  async load(): Promise<void> {
    const request = ++this.sequence;
    this.refreshing.set(true);
    this.error.set(null);
    try {
      const result = await this.fetch(this.page());
      if (request !== this.sequence) {
        return; // A newer request was started; its response wins.
      }
      const totalPages = Number(result.totalPages);
      const lastPage = Math.max(1, totalPages);
      if (this.page() > lastPage) {
        this.page.set(lastPage);
        return this.load();
      }
      this.items.set(result.items);
      this.totalPages.set(totalPages);
      this.totalCount.set(Number(result.totalCount));
    } catch (err) {
      if (request === this.sequence) {
        this.error.set(problemMessage(toProblem(err)));
      }
    } finally {
      if (request === this.sequence) {
        this.loading.set(false);
        this.refreshing.set(false);
      }
    }
  }

  goTo(page: number): Promise<void> {
    this.page.set(page);
    return this.load();
  }

  /** Back to the first page, e.g. after a filter change. */
  reset(): Promise<void> {
    return this.goTo(1);
  }
}
