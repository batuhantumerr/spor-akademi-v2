import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { api } from '../api/client';
import { errorMessage, problemFrom } from '../api/errors';
import { session } from '../auth/session';
import { formatDate, formatMoney, formatPeriod } from '../format/format';
import { fakeApi, json, parentUser, sessionResponse } from '../test/fake-api';
import { renderApp } from '../test/render-app';
import { LANGUAGE_STORAGE_KEY, getLanguage, initialLanguage, setLanguage } from './language';
import { dictionaries, text } from './tr';

type Node = string | readonly Node[] | { readonly [key: string]: Node };

function leaves(node: Node, path = ''): Map<string, string> {
  const result = new Map<string, string>();
  if (typeof node === 'string') {
    result.set(path, node);
  } else {
    for (const [key, child] of Object.entries(node)) {
      for (const [p, v] of leaves(child, path ? `${path}.${key}` : key)) result.set(p, v);
    }
  }
  return result;
}

const placeholders = (value: string) => [...value.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

describe('dictionaries (P3-10, DEC-426)', () => {
  for (const part of ['text', 'errors'] as const) {
    const tr = leaves(dictionaries.tr[part]);
    const en = leaves(dictionaries.en[part]);

    it(`${part}: same keys, same placeholders, no empty or Turkish English text`, () => {
      expect([...en.keys()].sort()).toEqual([...tr.keys()].sort());
      const placeholderMismatches = [...tr].filter(
        ([key, value]) => placeholders(value).join() !== placeholders(en.get(key) ?? '').join(),
      );
      expect(placeholderMismatches.map(([key]) => key)).toEqual([]);
      expect([...en].filter(([, value]) => value.trim() === '').map(([key]) => key)).toEqual([]);
      expect([...en].filter(([, value]) => /[çğıİöşüÇĞÖŞÜ]/.test(value)).map(([key]) => key)).toEqual([]);
    });
  }
});

describe('language choice', () => {
  const storage = (value: string | null) => ({ getItem: () => value });

  it('prefers the stored choice, then the browser language, then Turkish', () => {
    expect(initialLanguage(storage('en'), ['tr-TR'])).toBe('en');
    expect(initialLanguage(storage(null), ['en-US'])).toBe('en');
    expect(initialLanguage(storage('xx'), ['de'])).toBe('tr');
    expect(initialLanguage(null, [])).toBe('tr');
  });

  it('text and error messages follow the active language', () => {
    const nav = text.nav;
    expect(nav.children).toBe('Çocuklarım');
    setLanguage('en');
    expect(nav.children).toBe('My children');
    expect(errorMessage(problemFrom({ code: 'Auth.InvalidCredentials' }, 401))).toBe(
      'The email or password is incorrect.',
    );
  });

  it('formats dates and money in the active language; money stays TRY and exact', () => {
    expect(formatMoney('1250.00')).toBe('₺1.250,00');
    expect(formatDate('2027-01-04')).toBe('04.01.2027');
    setLanguage('en');
    expect(formatMoney('1250.00')).toBe('₺1,250.00');
    expect(formatMoney('12345678901234567.15')).toBe('₺12,345,678,901,234,567.15');
    expect(formatDate('2027-01-04')).toBe('04/01/2027');
    expect(formatPeriod('2026-11')).toBe('November 2026');
  });
});

describe('switching in the app', () => {
  it('re-renders the login page in English, sets <html lang> and remembers the choice', async () => {
    fakeApi();
    renderApp('/login');
    expect(await screen.findByRole('heading', { name: 'Veli girişi' })).toBeInTheDocument();
    expect(document.title).toBe('Veli girişi · Spor Akademi');

    await userEvent.click(screen.getByRole('button', { name: 'English' }));

    expect(await screen.findByRole('heading', { name: 'Parent sign-in' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Türkçe' })).toHaveAttribute('title', 'Change language');
    expect(getLanguage()).toBe('en');
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('en');
    expect(document.title).toBe('Parent sign-in · Spor Akademi');
  });

  it('sends the active language as Accept-Language to the API and the session endpoints', async () => {
    setLanguage('en');
    const fake = fakeApi({
      'POST /api/v1/auth/login': () => json(sessionResponse(parentUser())),
      'GET /api/v1/parent/children': () => json([]),
    });

    await session.login('veli@example.test', 'synthetic-password-1');
    await api.GET('/api/v1/parent/children');

    expect(fake.calls.map((request) => request.headers.get('Accept-Language'))).toEqual(['en', 'en']);
  });
});
