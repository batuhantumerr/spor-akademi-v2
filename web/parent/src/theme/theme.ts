import { useCallback, useSyncExternalStore } from 'react';

export type ThemePreference = 'light' | 'dark' | 'system';

/** Same key and classes as web/admin (core/theme/theme.service.ts) and the pre-paint script in index.html. */
export const THEME_STORAGE_KEY = 'sa-theme';

const listeners = new Set<() => void>();

export function readPreference(): ThemePreference {
  try {
    const stored = localStorage.getItem(THEME_STORAGE_KEY);
    return stored === 'light' || stored === 'dark' || stored === 'system' ? stored : 'system';
  } catch {
    return 'system';
  }
}

function systemDark(): boolean {
  return typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches;
}

export function isDark(preference: ThemePreference = readPreference()): boolean {
  return preference === 'system' ? systemDark() : preference === 'dark';
}

/** Light/dark as a class on <html>; the preference is a per-browser convenience. */
export function applyTheme(): void {
  const dark = isDark();
  const root = document.documentElement;
  root.classList.toggle('dark', dark);
  root.classList.toggle('light', !dark);
}

export function setPreference(preference: ThemePreference): void {
  try {
    localStorage.setItem(THEME_STORAGE_KEY, preference);
  } catch {
    // Storage can be unavailable (private mode); the theme still applies for this visit.
  }
  notify();
}

function notify(): void {
  applyTheme();
  for (const listener of listeners) listener();
}

/** The one MediaQueryList the listener is registered on; kept referenced so the browser cannot collect it. */
let systemQuery: MediaQueryList | null = null;

/**
 * One system-theme listener, on one MediaQueryList, while any component listens. Each matchMedia call returns a new
 * list: registering on one and removing from another left stale or lost listeners, and with two subscribers the first
 * unmount removed the shared listener (P3-11: the payment and offline pages kept a stale theme after a navigation).
 * Subscribing also applies the current theme, in case the system theme changed while nothing listened.
 */
function subscribe(listener: () => void): () => void {
  if (listeners.size === 0 && typeof window.matchMedia === 'function') {
    systemQuery = window.matchMedia('(prefers-color-scheme: dark)');
    systemQuery.addEventListener('change', notify);
    applyTheme();
  }
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0) {
      systemQuery?.removeEventListener('change', notify);
      systemQuery = null;
    }
  };
}

/** Current theme for components; re-renders when the preference or the system theme changes. */
export function useTheme(): { dark: boolean; toggle: () => void } {
  const dark = useSyncExternalStore(subscribe, () => isDark());
  const toggle = useCallback(() => {
    setPreference(isDark() ? 'light' : 'dark');
  }, []);
  return { dark, toggle };
}
