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

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  const media =
    typeof window.matchMedia === 'function' ? window.matchMedia('(prefers-color-scheme: dark)') : null;
  media?.addEventListener('change', notify);
  return () => {
    listeners.delete(listener);
    media?.removeEventListener('change', notify);
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
