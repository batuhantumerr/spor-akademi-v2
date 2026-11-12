import {
  ChangeDetectionStrategy,
  Component,
  Injectable,
  OnDestroy,
  computed,
  inject,
  signal,
} from '@angular/core';
import { LucideBell, LucideCircleAlert, LucideCircleCheck, LucideX } from '@lucide/angular';

import { ui } from '../../core/i18n/tr';

export interface Toast {
  id: number;
  kind: 'success' | 'error' | 'info';
  message: string;
}

interface Timer {
  handle: ReturnType<typeof setTimeout> | null;
  remaining: number;
  startedAt: number;
}

export const TOAST_DURATION_MS = { success: 4000, error: 7000, info: 6000 } as const;

/** Short-lived notifications for completed or failed actions. */
@Injectable({ providedIn: 'root' })
export class ToastService implements OnDestroy {
  private nextId = 1;
  private readonly timers = new Map<number, Timer>();
  private paused = false;
  readonly toasts = signal<Toast[]>([]);

  success(message: string): void {
    this.push('success', message);
  }

  error(message: string): void {
    this.push('error', message);
  }

  /** Something happened elsewhere (e.g. a real-time notification); shown once while it is visible. */
  info(message: string): void {
    if (this.toasts().some((t) => t.kind === 'info' && t.message === message)) {
      return;
    }
    this.push('info', message);
  }

  dismiss(id: number): void {
    const timer = this.timers.get(id);
    if (timer?.handle) {
      clearTimeout(timer.handle);
    }
    this.timers.delete(id);
    this.toasts.update((list) => list.filter((t) => t.id !== id));
  }

  /** Stops every countdown while the pointer or focus is on the toasts (WCAG 2.2.1). */
  pause(): void {
    if (this.paused) {
      return;
    }
    this.paused = true;
    const now = Date.now();
    for (const timer of this.timers.values()) {
      if (timer.handle) {
        clearTimeout(timer.handle);
        timer.handle = null;
        timer.remaining = Math.max(0, timer.remaining - (now - timer.startedAt));
      }
    }
  }

  resume(): void {
    if (!this.paused) {
      return;
    }
    this.paused = false;
    for (const [id, timer] of this.timers) {
      this.start(id, timer);
    }
  }

  ngOnDestroy(): void {
    for (const timer of this.timers.values()) {
      if (timer.handle) {
        clearTimeout(timer.handle);
      }
    }
    this.timers.clear();
  }

  private push(kind: Toast['kind'], message: string): void {
    const id = this.nextId++;
    this.toasts.update((list) => [...list, { id, kind, message }]);
    const timer: Timer = { handle: null, remaining: TOAST_DURATION_MS[kind], startedAt: 0 };
    this.timers.set(id, timer);
    if (!this.paused) {
      this.start(id, timer);
    }
  }

  private start(id: number, timer: Timer): void {
    timer.startedAt = Date.now();
    timer.handle = setTimeout(() => this.dismiss(id), timer.remaining);
  }
}

/**
 * Toast stack. Successes and notices are announced through one persistent polite live region;
 * errors are role="alert" so they interrupt. Nothing takes focus. Hovering or focusing the stack pauses the timers.
 */
@Component({
  selector: 'app-toasts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideBell, LucideCircleAlert, LucideCircleCheck, LucideX],
  template: `
    <div
      class="pointer-events-none fixed right-4 bottom-4 z-50 flex w-[min(24rem,calc(100vw-2rem))] flex-col gap-2"
      (mouseenter)="hold('pointer', true)"
      (mouseleave)="hold('pointer', false)"
      (focusin)="hold('focus', true)"
      (focusout)="hold('focus', false)"
    >
      @for (toast of errors(); track toast.id) {
        <div
          role="alert"
          class="pointer-events-auto flex items-start gap-3 rounded-xl border border-destructive/40 bg-card p-3 text-sm shadow-lg"
        >
          <svg lucideCircleAlert class="mt-0.5 size-4 shrink-0 text-destructive"></svg>
          <p class="flex-1 text-foreground">{{ toast.message }}</p>
          <button
            type="button"
            class="text-muted-foreground hover:text-foreground"
            [attr.aria-label]="common.close"
            (click)="toasts.dismiss(toast.id)"
          >
            <svg lucideX class="size-4"></svg>
          </button>
        </div>
      }
      <div role="status" aria-live="polite" class="flex flex-col gap-2">
        @for (toast of notices(); track toast.id) {
          <div
            class="pointer-events-auto flex items-start gap-3 rounded-xl border bg-card p-3 text-sm shadow-lg"
            [class]="toast.kind === 'info' ? 'border-primary/40' : 'border-success/40'"
            [attr.data-kind]="toast.kind"
          >
            @if (toast.kind === 'info') {
              <svg lucideBell class="mt-0.5 size-4 shrink-0 text-primary" aria-hidden="true"></svg>
            } @else {
              <svg lucideCircleCheck class="mt-0.5 size-4 shrink-0 text-success"></svg>
            }
            <p class="flex-1 text-foreground">{{ toast.message }}</p>
            <button
              type="button"
              class="text-muted-foreground hover:text-foreground"
              [attr.aria-label]="common.close"
              (click)="toasts.dismiss(toast.id)"
            >
              <svg lucideX class="size-4"></svg>
            </button>
          </div>
        }
      </div>
    </div>
  `,
})
export class ToastsComponent {
  protected readonly toasts = inject(ToastService);
  // A section view, not the string, so the label follows a language switch (DEC-426).
  protected readonly common = ui.common;
  protected readonly errors = computed(() =>
    this.toasts.toasts().filter((t) => t.kind === 'error'),
  );
  protected readonly notices = computed(() =>
    this.toasts.toasts().filter((t) => t.kind !== 'error'),
  );
  private readonly holds = new Set<'pointer' | 'focus'>();

  protected hold(reason: 'pointer' | 'focus', active: boolean): void {
    if (active) {
      this.holds.add(reason);
    } else {
      this.holds.delete(reason);
    }
    if (this.holds.size > 0) {
      this.toasts.pause();
    } else {
      this.toasts.resume();
    }
  }
}
