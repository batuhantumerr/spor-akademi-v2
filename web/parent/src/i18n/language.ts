import { useSyncExternalStore } from 'react';

/** P3-10 (DEC-426): the UI languages; Turkish is the default. Same rules and storage key as web/admin. */
export const LANGUAGES = ['tr', 'en'] as const;
export type Language = (typeof LANGUAGES)[number];

/** Same key as the pre-paint script in index.html. */
export const LANGUAGE_STORAGE_KEY = 'sa-language';

/** Each language's name in that language, as the switcher shows it (never translated). */
export const LANGUAGE_NAMES: Readonly<Record<Language, string>> = { tr: 'Türkçe', en: 'English' };

/** en-GB keeps day-first dates and the 24-hour clock. */
const LOCALES: Readonly<Record<Language, string>> = { tr: 'tr-TR', en: 'en-GB' };

function isLanguage(value: unknown): value is Language {
  return value === 'tr' || value === 'en';
}

function safeStorage(): Storage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}

function browserLanguages(): readonly string[] {
  if (typeof navigator === 'undefined') return [];
  return navigator.languages.length > 0 ? navigator.languages : [navigator.language];
}

/** The stored choice, else the browser's first preferred language when we support it, else Turkish. */
export function initialLanguage(
  storage: Pick<Storage, 'getItem'> | null = safeStorage(),
  preferred: readonly string[] = browserLanguages(),
): Language {
  try {
    const stored = storage?.getItem(LANGUAGE_STORAGE_KEY);
    if (isLanguage(stored)) return stored;
  } catch {
    // Blocked storage: fall through to the browser language.
  }
  const first = preferred[0]?.toLowerCase() ?? '';
  return first === 'en' || first.startsWith('en-') ? 'en' : 'tr';
}

let current: Language = initialLanguage();
const listeners = new Set<() => void>();

export function getLanguage(): Language {
  return current;
}

/** Intl locale of the active language (dates, numbers; NFR-CMP-03). */
export function locale(): string {
  return LOCALES[current];
}

/** Keeps `<html lang>` on the active language; called at startup and on every switch. */
export function applyLanguage(): void {
  document.documentElement.lang = current;
}

/** Switches the language for this browser; storage failures are ignored (a per-browser convenience). */
export function setLanguage(next: Language): void {
  if (next === current) return;
  current = next;
  try {
    localStorage.setItem(LANGUAGE_STORAGE_KEY, next);
  } catch {
    // Unavailable storage: the choice still applies until the tab is closed.
  }
  applyLanguage();
  for (const listener of listeners) listener();
}

export function subscribeLanguage(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The active language; components using it re-render on a switch. */
export function useLanguage(): Language {
  return useSyncExternalStore(subscribeLanguage, getLanguage, getLanguage);
}

/** A lazily created Intl formatter per locale. */
export function perLocale<T>(create: (locale: string) => T): () => T {
  const cache = new Map<string, T>();
  return () => {
    const key = locale();
    let value = cache.get(key);
    if (value === undefined) {
      value = create(key);
      cache.set(key, value);
    }
    return value;
  };
}
