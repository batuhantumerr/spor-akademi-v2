import { useSyncExternalStore } from 'react';

export interface Toast {
  readonly id: number;
  readonly message: string;
}

/** How long a toast stays; it is a heads-up, the page itself shows the refetched data. */
export const TOAST_TTL_MS = 6_000;
/** At most this many at once; the oldest goes first. */
export const MAX_TOASTS = 3;

/**
 * Short real-time notices (P3-08). A plain store, so the hub client can show one without React and tests can read
 * it. Toasts never take focus and disappear by themselves; ending the session clears them (they name children).
 */
export class ToastStore {
  private toasts: readonly Toast[] = [];
  private nextId = 1;
  private readonly timers = new Map<number, ReturnType<typeof setTimeout>>();
  private readonly listeners = new Set<() => void>();

  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  readonly getState = (): readonly Toast[] => this.toasts;

  show(message: string): void {
    const toast = { id: this.nextId++, message };
    const kept = [...this.toasts, toast];
    for (const dropped of kept.splice(0, Math.max(0, kept.length - MAX_TOASTS))) {
      this.clearTimer(dropped.id);
    }
    this.timers.set(
      toast.id,
      setTimeout(() => {
        this.dismiss(toast.id);
      }, TOAST_TTL_MS),
    );
    this.set(kept);
  }

  dismiss(id: number): void {
    this.clearTimer(id);
    if (this.toasts.some((toast) => toast.id === id)) {
      this.set(this.toasts.filter((toast) => toast.id !== id));
    }
  }

  clear(): void {
    for (const id of this.timers.keys()) this.clearTimer(id);
    if (this.toasts.length > 0) this.set([]);
  }

  private clearTimer(id: number): void {
    const timer = this.timers.get(id);
    if (timer !== undefined) clearTimeout(timer);
    this.timers.delete(id);
  }

  private set(toasts: readonly Toast[]): void {
    this.toasts = toasts;
    for (const listener of this.listeners) listener();
  }
}

export const toasts = new ToastStore();

export function useToasts(store: ToastStore = toasts): readonly Toast[] {
  return useSyncExternalStore(store.subscribe, store.getState);
}
