import { signal } from '@angular/core';

/** P3-10 (DEC-426): the UI languages. Turkish is the default; schools are Turkish. */
export const LANGUAGES = ['tr', 'en'] as const;
export type Language = (typeof LANGUAGES)[number];

/** Same key as the inline script in index.html, which sets `<html lang>` before Angular starts. */
export const LANGUAGE_STORAGE_KEY = 'sa-language';

/** Intl locale per language: en-GB keeps day-first dates and the 24-hour clock that Turkish users expect. */
const LOCALES: Readonly<Record<Language, string>> = { tr: 'tr-TR', en: 'en-GB' };

function isLanguage(value: unknown): value is Language {
  return value === 'tr' || value === 'en';
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
    // Storage can be blocked (private mode, policies); fall through to the browser language.
  }
  const first = preferred[0]?.toLowerCase() ?? '';
  return first === 'en' || first.startsWith('en-') ? 'en' : 'tr';
}

const current = signal<Language>(initialLanguage());

/** The active language; reading it inside a template or `computed` re-renders on a switch. */
export const language = current.asReadonly();

/** Intl locale of the active language (dates, numbers, sorting, case mapping; NFR-CMP-03). */
export function locale(): string {
  return LOCALES[current()];
}

/** Switches the language for this browser; a per-browser convenience, so storage failures are ignored. */
export function setLanguage(next: Language): void {
  current.set(next);
  try {
    localStorage.setItem(LANGUAGE_STORAGE_KEY, next);
  } catch {
    // Unavailable storage: the choice still applies until the tab is closed.
  }
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
  return navigator.languages?.length ? navigator.languages : [navigator.language ?? ''];
}
