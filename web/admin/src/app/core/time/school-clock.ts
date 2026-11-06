import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, computed, effect, inject, signal } from '@angular/core';

import { AuthService } from '../auth/auth.service';

/** How often "today" is re-checked, so it turns over at the school's midnight. */
export const CLOCK_TICK_MS = 60_000;

/**
 * The calendar date (<c>YYYY-MM-DD</c>) at <paramref name="now"/> in <paramref name="timeZone"/>, or in
 * the browser's zone when none is known.
 */
export function dateIn(now: Date, timeZone: string | null | undefined): string {
  if (timeZone) {
    try {
      const parts = new Intl.DateTimeFormat('en-CA', {
        timeZone,
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
      }).formatToParts(now);
      const part = (type: string) => parts.find((p) => p.type === type)?.value ?? '';
      return `${part('year')}-${part('month')}-${part('day')}`;
    } catch {
      // Unknown zone in this browser: fall back to the local date below.
    }
  }
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/**
 * "Today" in the school's time zone (from GET /me), not the browser's: a coach abroad or a laptop
 * on UTC still sees the school's day. Re-checked every minute and when the window regains focus,
 * so a page left open overnight moves to the new day. The server stays authoritative.
 */
@Injectable({ providedIn: 'root' })
export class SchoolClock {
  private readonly auth = inject(AuthService);
  private readonly document = inject(DOCUMENT);

  readonly timeZone = computed(() => this.auth.user()?.schoolTimeZone ?? null);
  private readonly current = signal(this.compute());
  /** Today's date in the school's time zone. */
  readonly today = this.current.asReadonly();

  constructor() {
    const refresh = () => this.refresh();
    const onVisible = () => {
      if (this.document.visibilityState !== 'hidden') {
        refresh();
      }
    };
    const timer = setInterval(refresh, CLOCK_TICK_MS);
    const view = this.document.defaultView;
    view?.addEventListener('focus', refresh);
    this.document.addEventListener('visibilitychange', onVisible);
    inject(DestroyRef).onDestroy(() => {
      clearInterval(timer);
      view?.removeEventListener('focus', refresh);
      this.document.removeEventListener('visibilitychange', onVisible);
    });
    // The zone arrives with the user (after login or session restore).
    effect(() => {
      this.timeZone();
      refresh();
    });
  }

  refresh(): void {
    const today = this.compute();
    if (today !== this.current()) {
      this.current.set(today);
    }
  }

  private compute(): string {
    return dateIn(new Date(), this.auth.user()?.schoolTimeZone);
  }
}
