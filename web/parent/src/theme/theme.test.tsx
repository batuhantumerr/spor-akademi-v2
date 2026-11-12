import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { text } from '../i18n/tr';
import { renderApp } from '../test/render-app';
import { applyTheme, readPreference, THEME_STORAGE_KEY, useTheme } from './theme';

function mockSystemDark(dark: boolean): void {
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({
      matches: dark && query === '(prefers-color-scheme: dark)',
      media: query,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    })),
  );
}

describe('theme', () => {
  beforeEach(() => {
    mockSystemDark(false);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('follows the system theme when nothing is stored', () => {
    mockSystemDark(true);

    applyTheme();

    expect(readPreference()).toBe('system');
    expect(document.documentElement).toHaveClass('dark');
    expect(document.documentElement).not.toHaveClass('light');
  });

  it('uses the stored preference over the system theme', () => {
    mockSystemDark(true);
    localStorage.setItem(THEME_STORAGE_KEY, 'light');

    applyTheme();

    expect(document.documentElement).toHaveClass('light');
  });

  it('ignores an unknown stored value', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'neon');

    expect(readPreference()).toBe('system');
  });

  it('toggles between light and dark from the header and remembers the choice', async () => {
    renderApp('/');
    const toggle = await screen.findByRole('button', { name: text.theme.toggle });
    expect(toggle).toHaveAttribute('title', text.theme.dark);

    await userEvent.click(toggle);

    expect(document.documentElement).toHaveClass('dark');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
    expect(toggle).toHaveAttribute('title', text.theme.light);

    await userEvent.click(toggle);

    expect(document.documentElement).toHaveClass('light');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');
  });
});

describe('theme subscription (P3-11)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('keeps following the system theme when one of two components stops listening', () => {
    // One MediaQueryList like the browser's: a listener registered twice is kept once.
    const registered = new Set<() => void>();
    let dark = false;
    const list = {
      get matches() {
        return dark;
      },
      media: '(prefers-color-scheme: dark)',
      addEventListener: (_: string, listener: () => void) => registered.add(listener),
      removeEventListener: (_: string, listener: () => void) => registered.delete(listener),
    };
    vi.stubGlobal(
      'matchMedia',
      vi.fn(() => list),
    );
    function Listener() {
      useTheme();
      return null;
    }
    const layout = render(<Listener />);
    const page = render(<Listener />);
    page.unmount();

    dark = true;
    act(() => {
      for (const listener of registered) listener();
    });

    expect(document.documentElement).toHaveClass('dark');
    layout.unmount();
    expect(registered.size).toBe(0);
  });
});
