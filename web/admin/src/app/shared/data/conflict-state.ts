import { signal } from '@angular/core';

import { problemMessage, toProblem } from '../../core/api/problem';

/**
 * State of a 409 Concurrency.Conflict while the user's edits are kept: whether the conflict is
 * open, the server's fresh version once loaded, and a load error. Only the latest load of a still
 * open conflict may set the result, so a slow response never lands after the user moved on.
 */
export class ConflictState<T> {
  readonly open = signal(false);
  readonly fresh = signal<T | null>(null);
  readonly error = signal<string | null>(null);
  private token = 0;

  constructor(
    private readonly fetch: () => Promise<T>,
    private readonly describeError: (err: unknown) => string = (err) =>
      problemMessage(toProblem(err)),
  ) {}

  /** Opens the conflict and loads the server version next to the user's values. */
  async start(): Promise<void> {
    this.open.set(true);
    this.fresh.set(null);
    await this.load();
  }

  /** (Re)loads the fresh version; ignored once the conflict is closed or a newer load started. */
  async load(): Promise<void> {
    if (!this.open()) {
      return;
    }
    const token = ++this.token;
    this.error.set(null);
    try {
      const fresh = await this.fetch();
      if (this.isCurrent(token)) {
        this.fresh.set(fresh);
      }
    } catch (err) {
      if (this.isCurrent(token)) {
        this.error.set(this.describeError(err));
      }
    }
  }

  /** Closes the conflict and returns the fresh version it had loaded (null if none). */
  close(): T | null {
    const fresh = this.fresh();
    this.token++;
    this.open.set(false);
    this.fresh.set(null);
    this.error.set(null);
    return fresh;
  }

  private isCurrent(token: number): boolean {
    return token === this.token && this.open();
  }
}
