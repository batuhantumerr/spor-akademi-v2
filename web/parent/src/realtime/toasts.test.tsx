import { act, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { childRoutes, fakeApi, signIn } from '../test/fake-api';
import { renderApp } from '../test/render-app';
import { ToastRegion } from './ToastRegion';
import { MAX_TOASTS, TOAST_TTL_MS, ToastStore, toasts } from './toasts';

afterEach(() => {
  act(() => {
    toasts.clear();
  });
  vi.useRealTimers();
});

describe('toasts (P3-08)', () => {
  it('announces politely, takes no focus and disappears by itself', () => {
    vi.useFakeTimers();
    render(<ToastRegion />);
    const region = document.querySelector('[aria-live]');
    expect(region).toHaveAttribute('aria-live', 'polite');
    const focused = document.activeElement;

    act(() => {
      toasts.show('Ada bugün antrenmana katıldı');
    });
    expect(region).toHaveTextContent('Ada bugün antrenmana katıldı');
    expect(document.activeElement).toBe(focused);
    expect(screen.queryByRole('button')).toBeNull();

    act(() => {
      vi.advanceTimersByTime(TOAST_TTL_MS);
    });
    expect(screen.queryByText('Ada bugün antrenmana katıldı')).toBeNull();
  });

  it('shows on the pages of the signed-in shell', async () => {
    fakeApi(childRoutes({}));
    signIn();
    renderApp('/');
    await screen.findByRole('heading', { name: 'Çocuklarım' });
    act(() => {
      toasts.show('Yeni duyuru yayımlandı');
    });
    expect(document.querySelector('[aria-live="polite"]')).toHaveTextContent('Yeni duyuru yayımlandı');
  });

  it('keeps only the newest few', () => {
    const store = new ToastStore();
    for (let i = 1; i <= MAX_TOASTS + 2; i++) store.show(`Bildirim ${String(i)}`);
    expect(store.getState().map((toast) => toast.message)).toEqual([
      'Bildirim 3',
      'Bildirim 4',
      'Bildirim 5',
    ]);
    store.clear();
    expect(store.getState()).toEqual([]);
  });
});
