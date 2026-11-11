import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { text } from '../i18n/tr';
import * as checkout from '../parent/checkout';
import { fakeApi, json, problem } from '../test/fake-api';
import { renderApp } from '../test/render-app';

const CONFIRM = 'POST /api/v1/auth/password-reset/confirm';
const PASSWORD = 'uzun-ve-guclu-bir-sifre';

describe('invitation / reset link page', () => {
  it('reads the token from the fragment, removes it from the address bar and sets the password', async () => {
    window.history.replaceState(null, '', '/reset-password#token=davet-123');
    const api = fakeApi({ [CONFIRM]: () => new Response(null, { status: 204 }) });
    renderApp('/reset-password');

    expect(await screen.findByRole('heading', { name: text.resetPassword.title })).toBeInTheDocument();
    expect(window.location.hash).toBe('');
    expect(window.location.pathname).toBe('/reset-password');

    await userEvent.type(screen.getByLabelText(text.resetPassword.next), PASSWORD);
    await userEvent.type(screen.getByLabelText(text.resetPassword.confirm), PASSWORD);
    await userEvent.click(screen.getByRole('button', { name: text.resetPassword.submit }));

    expect(await screen.findByText(text.resetPassword.success)).toBeInTheDocument();
    expect(await api.callsTo(CONFIRM)[0]!.json()).toEqual({ token: 'davet-123', newPassword: PASSWORD });
    expect(screen.getByRole('link', { name: text.resetPassword.toLogin })).toHaveAttribute('href', '/login');
  });

  it('checks length and match before calling the API', async () => {
    window.history.replaceState(null, '', '/reset-password#token=davet-123');
    const api = fakeApi({});
    renderApp('/reset-password');

    await userEvent.type(await screen.findByLabelText(text.resetPassword.next), 'kisa');
    await userEvent.click(screen.getByRole('button', { name: text.resetPassword.submit }));
    expect(screen.getByText(/en az 12 karakter olmalı/)).toBeInTheDocument();

    await userEvent.clear(screen.getByLabelText(text.resetPassword.next));
    await userEvent.type(screen.getByLabelText(text.resetPassword.next), PASSWORD);
    await userEvent.type(screen.getByLabelText(text.resetPassword.confirm), `${PASSWORD}x`);
    await userEvent.click(screen.getByRole('button', { name: text.resetPassword.submit }));
    expect(screen.getByText(text.resetPassword.mismatch)).toBeInTheDocument();
    expect(api.calls).toHaveLength(0);
  });

  it('offers a new link when the token was used or expired', async () => {
    window.history.replaceState(null, '', '/reset-password#token=eski');
    fakeApi({ [CONFIRM]: () => problem(422, 'Auth.ResetTokenInvalid') });
    renderApp('/reset-password');

    await userEvent.type(await screen.findByLabelText(text.resetPassword.next), PASSWORD);
    await userEvent.type(screen.getByLabelText(text.resetPassword.confirm), PASSWORD);
    await userEvent.click(screen.getByRole('button', { name: text.resetPassword.submit }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Bu bağlantı geçersiz');
    expect(screen.getByRole('link', { name: text.resetPassword.requestNew })).toHaveAttribute(
      'href',
      '/forgot-password',
    );
  });

  it('says so when the link has no token', async () => {
    fakeApi({});
    renderApp('/reset-password');

    expect(await screen.findByText(text.resetPassword.missingToken)).toBeInTheDocument();
  });
});

describe('forgot password', () => {
  it('always answers the same way, registered address or not', async () => {
    const api = fakeApi({
      'POST /api/v1/auth/password-reset/request': () => new Response(null, { status: 202 }),
    });
    renderApp('/forgot-password');

    await userEvent.type(await screen.findByLabelText(text.forgotPassword.email), 'veli@example.test');
    await userEvent.click(screen.getByRole('button', { name: text.forgotPassword.submit }));

    expect(await screen.findByText(text.forgotPassword.sent)).toBeInTheDocument();
    expect(await api.calls[0]!.json()).toEqual({ email: 'veli@example.test' });
  });
});

describe('payment pages', () => {
  it('the sandbox page completes the checkout and posts the token to the callback', async () => {
    const post = vi.spyOn(checkout, 'postToCallback').mockImplementation(() => undefined);
    const api = fakeApi({
      'POST /api/v1/sandbox-checkout/complete': () => json({ callbackUrl: '/api/v1/payment-callbacks' }),
    });
    renderApp('/sandbox-odeme?token=tok-1');

    await userEvent.click(await screen.findByRole('button', { name: text.sandbox.pay }));

    await vi.waitFor(() => {
      expect(post).toHaveBeenCalledWith('/api/v1/payment-callbacks', 'tok-1');
    });
    expect(await api.calls[0]!.json()).toEqual({ token: 'tok-1', succeed: true });
  });

  it.each([
    ['Succeeded', 'Ödemeniz alındı. Teşekkür ederiz.'],
    ['Failed', 'Ödeme tamamlanamadı.'],
    ['bogus', 'Ödeme bulunamadı.'],
  ])('the result page shows %s and links back to the children', async (status, message) => {
    fakeApi({});
    renderApp(`/odeme-sonucu?durum=${status}`);

    expect(await screen.findByTestId('result-message')).toHaveTextContent(message);
    expect(screen.getByRole('link', { name: 'Çocuklarıma dön' })).toHaveAttribute('href', '/');
  });
});
