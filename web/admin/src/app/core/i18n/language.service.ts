import { DOCUMENT } from '@angular/common';
import { Injectable, effect, inject } from '@angular/core';

import { LANGUAGES, Language, language, setLanguage } from './language';

/** Each language's name in that language, as the switcher shows it (never translated). */
export const LANGUAGE_NAMES: Readonly<Record<Language, string>> = { tr: 'Türkçe', en: 'English' };

/** Keeps `<html lang>` on the active language and switches it (P3-10, DEC-426). */
@Injectable({ providedIn: 'root' })
export class LanguageService {
  private readonly document = inject(DOCUMENT);

  readonly current = language;

  constructor() {
    this.document.documentElement.lang = language();
    effect(() => {
      this.document.documentElement.lang = language();
    });
  }

  /** The language the switcher offers: with two languages, simply the other one. */
  other(): Language {
    return LANGUAGES.find((l) => l !== language()) ?? 'tr';
  }

  set(next: Language): void {
    setLanguage(next);
    // Set right away as well, so assistive technology reads the new text in the new language without waiting.
    this.document.documentElement.lang = next;
  }
}
