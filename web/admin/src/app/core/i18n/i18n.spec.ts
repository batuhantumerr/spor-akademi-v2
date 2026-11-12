import { Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { Router, TitleStrategy, provideRouter } from '@angular/router';

import { formatDate, formatMoney, formatPercent } from '../format/format';
import { LanguageSwitcherComponent } from '../../shared/ui/language-switcher.component';
import { LANGUAGE_STORAGE_KEY, initialLanguage, language, setLanguage } from './language';
import { LanguageService } from './language.service';
import { localized } from './localized';
import { AppTitleStrategy } from './title.strategy';
import { dictionaries, errorMessages, ui } from './tr';

type Node = string | readonly Node[] | { readonly [key: string]: Node };

/** Every leaf path with its value, e.g. `nav.groups` → "Gruplar"; array items by index. */
function leaves(node: Node, path = ''): Map<string, string> {
  const result = new Map<string, string>();
  if (typeof node === 'string') {
    result.set(path, node);
  } else {
    for (const [key, child] of Object.entries(node)) {
      for (const [p, v] of leaves(child as Node, path ? `${path}.${key}` : key)) result.set(p, v);
    }
  }
  return result;
}

const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

describe('dictionaries (P3-10, DEC-426)', () => {
  for (const part of ['ui', 'errors'] as const) {
    describe(part, () => {
      const tr = leaves(dictionaries.tr[part] as Node);
      const en = leaves(dictionaries.en[part] as Node);

      it('have exactly the same keys, including the open maps and list lengths', () => {
        expect([...en.keys()].sort()).toEqual([...tr.keys()].sort());
      });

      it('use the same placeholders in both languages', () => {
        const mismatches = [...tr].filter(
          ([key, text]) => placeholders(text).join() !== placeholders(en.get(key) ?? '').join(),
        );
        expect(mismatches.map(([key]) => key)).toEqual([]);
      });

      it('have no empty texts', () => {
        expect([...en].filter(([, text]) => text.trim() === '').map(([key]) => key)).toEqual([]);
      });

      it('have no Turkish letters in English', () => {
        // Proper nouns in examples ("Ayşe Yılmaz") are the only allowed exception.
        const turkish = [...en].filter(
          ([key, text]) => /[çğıİöşüÇĞÖŞÜ]/.test(text) && key !== 'students.searchPlaceholder',
        );
        expect(turkish.map(([key]) => key)).toEqual([]);
      });
    });
  }
});

describe('localized text', () => {
  it('follows the active language, also through a captured section', () => {
    const nav = ui.nav;
    expect(nav.groups).toBe('Gruplar');
    setLanguage('en');
    expect(nav.groups).toBe('Groups');
    expect(ui.attendance.statuses['Present']).toBe('Present');
    expect(errorMessages['Auth.Forbidden']).toBe('You don’t have permission to do this.');
  });

  it('behaves like a plain object for keys, membership and iteration', () => {
    setLanguage('en');
    expect(Object.keys(ui.roles)).toEqual(['PlatformAdmin', 'SchoolAdmin', 'Coach']);
    expect('Auth.Forbidden' in errorMessages).toBe(true);
    expect(errorMessages['No.Such.Code']).toBeUndefined();
    expect(Array.isArray(ui.login.highlights)).toBe(true);
    expect(Object.entries(ui.table)[0]).toEqual(['name', 'Name']);
  });

  it('falls back to Turkish for a key missing in an open map, and cannot be changed', () => {
    let active: 'tr' | 'en' = 'en';
    const text = localized<{ map: Record<string, string> }>(
      { tr: { map: { a: 'A-tr', b: 'B-tr' } }, en: { map: { a: 'A-en' } } },
      () => active,
    );
    expect(text.map['a']).toBe('A-en');
    expect(text.map['b']).toBe('B-tr');
    active = 'tr';
    expect(text.map['a']).toBe('A-tr');
    expect(() => {
      'use strict';
      (text.map as Record<string, string>)['a'] = 'x';
    }).toThrow();
  });
});

describe('language choice', () => {
  const storage = (value: string | null) => ({ getItem: () => value });

  it('prefers the stored choice, then the browser language, then Turkish', () => {
    expect(initialLanguage(storage('en'), ['tr-TR'])).toBe('en');
    expect(initialLanguage(storage('tr'), ['en-US'])).toBe('tr');
    expect(initialLanguage(storage('fr'), ['en-GB', 'tr'])).toBe('en');
    expect(initialLanguage(storage(null), ['EN'])).toBe('en');
    expect(initialLanguage(storage(null), ['de-DE', 'en'])).toBe('tr');
    expect(initialLanguage(null, [])).toBe('tr');
  });

  it('survives blocked storage', () => {
    const blocked = {
      getItem: () => {
        throw new Error('SecurityError');
      },
    };
    expect(initialLanguage(blocked, ['en-US'])).toBe('en');
  });

  it('persists a switch in this browser', () => {
    setLanguage('en');
    expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('en');
    expect(language()).toBe('en');
  });
});

describe('formats per language (money always TRY)', () => {
  it('formats in Turkish', () => {
    expect(formatMoney('1250.00')).toBe('₺1.250,00');
    expect(formatDate('2027-01-04')).toBe('04.01.2027');
    expect(formatPercent(0.42)).toBe('%42');
  });

  it('formats in English (en-GB) without floating point on money', () => {
    setLanguage('en');
    expect(formatMoney('1250.00')).toBe('₺1,250.00');
    expect(formatMoney('12345678901234567.15')).toBe('₺12,345,678,901,234,567.15');
    expect(formatDate('2027-01-04')).toBe('04/01/2027');
    expect(formatPercent(0.42)).toBe('42%');
  });
});

@Component({ template: '' })
class BlankPage {}

describe('switching in the app', () => {
  it('updates <html lang>, the switcher and the page title without navigating', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: 'groups', component: BlankPage, title: () => ui.nav.groups }]),
        { provide: TitleStrategy, useClass: AppTitleStrategy },
      ],
    });
    TestBed.inject(LanguageService);
    await TestBed.inject(Router).navigateByUrl('/groups');
    const title = TestBed.inject(Title);
    expect(title.getTitle()).toBe('Gruplar · Spor Akademi');
    expect(document.documentElement.lang).toBe('tr');

    const fixture = TestBed.createComponent(LanguageSwitcherComponent);
    await fixture.whenStable();
    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    expect(button.textContent?.trim()).toBe('English');
    expect(button.querySelector('span')?.getAttribute('lang')).toBe('en');

    button.click();
    await fixture.whenStable();
    TestBed.tick();
    expect(language()).toBe('en');
    expect(document.documentElement.lang).toBe('en');
    expect(title.getTitle()).toBe('Groups · Spor Akademi');
    expect(button.textContent?.trim()).toBe('Türkçe');
    expect(button.getAttribute('title')).toBe('Change language');
  });
});
