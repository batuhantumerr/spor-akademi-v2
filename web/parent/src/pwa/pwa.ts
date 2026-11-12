import { useSyncExternalStore } from 'react';

/**
 * The app's side of the service worker (P3-07, DEC-394): registration in production builds, the "Yeni sürüm hazır"
 * update flow and the install prompt.
 *
 * Updates: a new worker installs in the background and waits. The app shows a banner; "Yenile" tells the waiting
 * worker to take over and reloads once it controls the page. Nothing switches versions under a parent who is in
 * the middle of something (a payment, a form), and the old worker keeps serving the old version's files until then.
 */

/** Chromium's install prompt event (not in the DOM typings). */
export interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<unknown>;
}

export interface PwaState {
  /** A new version is installed and waiting for "Yenile". */
  readonly updateReady: boolean;
  /** The browser offered an install prompt (Chromium only) that has not been used yet. */
  readonly installable: boolean;
}

/** Looks for a new version when a long-open app comes back to the foreground, at most this often. */
const UPDATE_CHECK_INTERVAL_MS = 60 * 60 * 1000;

export class Pwa {
  private state: PwaState = { updateReady: false, installable: false };
  private readonly listeners = new Set<() => void>();
  private registration: ServiceWorkerRegistration | null = null;
  private installPrompt: BeforeInstallPromptEvent | null = null;
  private lastUpdateCheck = 0;

  constructor(
    private readonly win: Window = window,
    private readonly reload: () => void = () => {
      window.location.reload();
    },
  ) {}

  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  readonly getState = (): PwaState => this.state;

  /** Registers `/sw.js` after the page has loaded (production builds only, main.tsx). */
  start(): void {
    const container = this.win.navigator.serviceWorker as ServiceWorkerContainer | undefined;
    if (!container) return;
    this.win.addEventListener('beforeinstallprompt', (event) => {
      event.preventDefault(); // no mini-infobar; the app offers a dismissible hint instead
      this.installPrompt = event as BeforeInstallPromptEvent;
      this.setState({ installable: true });
    });
    this.win.addEventListener('appinstalled', () => {
      this.installPrompt = null;
      this.setState({ installable: false });
    });
    const register = () => {
      // `updateViaCache: 'none'`: the browser always asks the server for /sw.js (nginx also sends no-cache).
      container
        .register('/sw.js', { scope: '/', updateViaCache: 'none' })
        .then((registration) => {
          this.watch(registration, container);
        })
        .catch(() => {
          // No worker (unsupported, private mode, certificate error): the app works online exactly as before.
        });
    };
    if (this.win.document.readyState === 'complete') register();
    else this.win.addEventListener('load', register, { once: true });
  }

  /** Follows a registration for waiting versions. Public for tests. */
  watch(registration: ServiceWorkerRegistration, container: ServiceWorkerContainer): void {
    this.registration = registration;
    this.lastUpdateCheck = Date.now();
    // On the first install there is no controller yet: that is not an update.
    if (registration.waiting && container.controller) this.setState({ updateReady: true });
    registration.addEventListener('updatefound', () => {
      const installing = registration.installing;
      installing?.addEventListener('statechange', () => {
        if (installing.state === 'installed' && container.controller) this.setState({ updateReady: true });
      });
    });
    this.win.document.addEventListener('visibilitychange', () => {
      if (this.win.document.visibilityState !== 'visible') return;
      if (Date.now() - this.lastUpdateCheck < UPDATE_CHECK_INTERVAL_MS) return;
      this.lastUpdateCheck = Date.now();
      registration.update().catch(() => {
        // Offline: the next check will try again.
      });
    });
  }

  /** "Yenile": the waiting version takes over, then the page reloads once under it. */
  applyUpdate(): void {
    const waiting = this.registration?.waiting;
    const container = this.win.navigator.serviceWorker as ServiceWorkerContainer | undefined;
    if (!waiting || !container) {
      this.reload();
      return;
    }
    container.addEventListener(
      'controllerchange',
      () => {
        this.reload();
      },
      { once: true },
    );
    waiting.postMessage({ type: 'SKIP_WAITING' });
  }

  /** Shows the browser's own install dialog; the event can be used once. */
  async install(): Promise<void> {
    const prompt = this.installPrompt;
    this.installPrompt = null;
    this.setState({ installable: false });
    if (!prompt) return;
    try {
      await prompt.prompt();
    } catch {
      // Already used or dismissed by the browser.
    }
  }

  private setState(change: Partial<PwaState>): void {
    this.state = { ...this.state, ...change };
    for (const listener of this.listeners) listener();
  }
}

export const pwa = new Pwa();

export function usePwa(): PwaState {
  return useSyncExternalStore(pwa.subscribe, pwa.getState);
}

function subscribeOnline(listener: () => void): () => void {
  window.addEventListener('online', listener);
  window.addEventListener('offline', listener);
  return () => {
    window.removeEventListener('online', listener);
    window.removeEventListener('offline', listener);
  };
}

/** The browser's idea of the connection; `false` means certainly offline. */
export function useOnline(): boolean {
  return useSyncExternalStore(subscribeOnline, () => navigator.onLine);
}

/** When the page last reloaded itself over a missing chunk (epoch ms, this tab only). */
export const CHUNK_RELOAD_STORAGE_KEY = 'sa-chunk-reload';
const CHUNK_RELOAD_MIN_INTERVAL_MS = 60 * 1000;

/**
 * A lazily loaded chunk that is gone from the server (a deployment removed the old hashed files while this tab ran
 * the old version) fails with Vite's `vite:preloadError`. The page reloads once to get the current version; a
 * second failure within a minute is left to the error page, so a broken deployment cannot cause a reload loop.
 * Without storage (private mode) it never reloads.
 */
export function reloadOnceOnChunkError(
  win: Window = window,
  reload: () => void = () => {
    window.location.reload();
  },
  now: () => number = Date.now,
): void {
  win.addEventListener('vite:preloadError', (event) => {
    try {
      const last = Number(win.sessionStorage.getItem(CHUNK_RELOAD_STORAGE_KEY) ?? '0');
      if (now() - last < CHUNK_RELOAD_MIN_INTERVAL_MS) return;
      win.sessionStorage.setItem(CHUNK_RELOAD_STORAGE_KEY, String(now()));
    } catch {
      return;
    }
    event.preventDefault(); // handled: do not throw the import error
    reload();
  });
}

/** The install hint is dismissed for good in this browser (not personal data). */
export const INSTALL_HINT_STORAGE_KEY = 'sa-install-hint';

export function installHintDismissed(): boolean {
  try {
    return localStorage.getItem(INSTALL_HINT_STORAGE_KEY) === 'dismissed';
  } catch {
    return false;
  }
}

export function dismissInstallHint(): void {
  try {
    localStorage.setItem(INSTALL_HINT_STORAGE_KEY, 'dismissed');
  } catch {
    // Storage unavailable: the hint stays hidden for this visit only.
  }
}
