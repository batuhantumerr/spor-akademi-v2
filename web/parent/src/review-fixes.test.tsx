import { act, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { safeReturnUrl } from './auth/links';
import { Session } from './auth/session';
import { nextSlot, schoolNow } from './format/school-time';
import { text } from './i18n/tr';
import * as checkout from './parent/checkout';
import {
  CHILD_ID,
  childRoutes,
  due,
  ENROLLMENT_ID,
  fakeApi,
  json,
  parentUser,
  sessionResponse,
  signIn,
} from './test/fake-api';
import { renderApp } from './test/render-app';

/** PR #91 review: the fixes and the cases they guard. */

describe('sandbox callback', () => {
  it('posts the token only to a callback on this origin', async () => {
    const post = vi.spyOn(checkout, 'postToCallback').mockImplementation(() => undefined);
    fakeApi({
      'POST /api/v1/sandbox-checkout/complete': () => json({ callbackUrl: 'https://evil.example/collect' }),
    });
    renderApp('/sandbox-odeme?token=tok-1');

    await userEvent.click(await screen.findByRole('button', { name: text.sandbox.pay }));

    expect(await screen.findByRole('alert')).toHaveTextContent(text.sandbox.badCallback);
    expect(post).not.toHaveBeenCalled();
    expect(checkout.callbackTarget('/api/v1/payment-callbacks')).toBe(
      `${window.location.origin}/api/v1/payment-callbacks`,
    );
    expect(checkout.callbackTarget('//evil.example/x')).toBeNull();
    expect(checkout.callbackTarget('javascript:alert(1)')).toBeNull();
  });
});

describe('pay button after Back', () => {
  it('is enabled again when the page comes back from the back/forward cache', async () => {
    signIn();
    vi.spyOn(checkout.browser, 'assign').mockImplementation(() => undefined);
    fakeApi({
      ...childRoutes({ dues: [due()] }),
      'POST /api/v1/parent/online-payments': () =>
        json(
          {
            id: 'p',
            enrollmentId: ENROLLMENT_ID,
            studentId: CHILD_ID,
            groupId: 'g',
            groupName: 'U10 Yüzme',
            period: '2026-11',
            amount: { amount: '900.00', currency: 'TRY' },
            status: 'Pending',
            checkoutUrl: '/sandbox-odeme?token=t',
            startedAt: '2026-11-11T09:00:00Z',
            checkoutExpiresAt: null,
            completedAt: null,
          },
          201,
        ),
    });
    renderApp(`/cocuk/${CHILD_ID}`);
    const pay = await screen.findByRole('button', { name: /aidatını online öde/ });

    await userEvent.click(pay);
    await vi.waitFor(() => {
      expect(pay).toBeDisabled();
    });

    act(() => {
      window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
    });
    expect(pay).toBeEnabled();
  });
});

describe('refresh in flight', () => {
  it('a refresh dropped by logout does not clear the newer refresh that replaced it', async () => {
    const answers: ((response: Response) => void)[] = [];
    const api = fakeApi({
      'POST /api/v1/auth/refresh': () => new Promise<Response>((resolve) => answers.push(resolve)),
      'POST /api/v1/auth/logout': () => new Response(null, { status: 204 }),
    });
    const session = new Session();
    session.adopt('a', parentUser());

    const first = session.refresh();
    await session.logout();
    session.adopt('b', parentUser());
    const second = session.refresh();
    answers[0]?.(json(sessionResponse(parentUser(), 'late')));
    await first;

    // The first call settled after the second started: concurrent callers must still share the second one.
    expect(session.refresh()).toBe(second);
    answers[1]?.(json(sessionResponse(parentUser(), 'c')));
    expect(await second).toBe('c');
    expect(api.callsTo('POST /api/v1/auth/refresh')).toHaveLength(2);
  });
});

describe('return URL', () => {
  it.each([
    ['/cocuk/1?x=1#y', '/cocuk/1?x=1#y'],
    [null, '/'],
    ['https://evil.example', '/'],
    ['//evil.example', '/'],
    ['/\\evil.example', '/'],
    ['/\t/evil.example', '/'],
    ['/%0a/evil', '/%0a/evil'],
    ['/login', '/'],
    ['/login?returnUrl=%2Flogin', '/'],
    ['/forgot-password', '/'],
    ['/reset-password', '/'],
  ])('%s → %s', (value, expected) => {
    expect(safeReturnUrl(value)).toBe(expected);
  });
});

describe('weekly slots', () => {
  it('skips a slot whose end is not after its start (the API never sends one)', () => {
    const now = schoolNow(new Date('2026-11-11T12:30:00Z'), 'Europe/Istanbul'); // Wednesday 15:30
    const upcoming = nextSlot(
      [
        { item: 'overnight', slots: [{ day: 'Wednesday', start: '22:00', end: '01:00' }] },
        { item: 'friday', slots: [{ day: 'Friday', start: '17:00', end: '18:00' }] },
      ],
      now,
    );
    expect(upcoming?.item).toBe('friday');
  });
});

describe('payment result', () => {
  it('says a successful payment shows in the list once the provider confirms it', async () => {
    fakeApi({});
    renderApp('/odeme-sonucu?durum=Succeeded');

    expect(await screen.findByTestId('result-note')).toHaveTextContent('ödemeler listesinde görünür');
  });
});
