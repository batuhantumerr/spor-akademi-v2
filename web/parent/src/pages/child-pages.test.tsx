import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { schoolNow } from '../format/school-time';
import { text } from '../i18n/tr';
import { browser } from '../parent/checkout';
import {
  attendance,
  CHILD_ID,
  child,
  childRoutes,
  due,
  ENROLLMENT_ID,
  fakeApi,
  group,
  json,
  problem,
  signIn,
} from '../test/fake-api';
import { renderApp } from '../test/render-app';

// The weekday name comes from Intl, typed as a plain string; the cast only satisfies the generated enum.
const today = schoolNow(new Date(), 'Europe/Istanbul');
const PAY = 'POST /api/v1/parent/online-payments';

function onlinePayment(checkoutUrl: string | null) {
  return {
    id: '0190a1b2-0000-7000-8000-0000000000f1',
    enrollmentId: ENROLLMENT_ID,
    studentId: CHILD_ID,
    groupId: 'g',
    groupName: 'U10 Yüzme',
    period: '2026-11',
    amount: { amount: '900.00', currency: 'TRY' },
    status: 'Pending',
    checkoutUrl,
    startedAt: '2026-11-11T09:00:00Z',
    checkoutExpiresAt: '2026-11-11T09:30:00Z',
    completedAt: null,
  };
}

describe('Çocuklarım', () => {
  it("shows each child's next training, today's attendance and what is owed", async () => {
    signIn();
    fakeApi(
      childRoutes({
        schedule: [group({ slots: [{ day: today.day as 'Monday', start: '00:00', end: '23:59' }] })],
        attendance: attendance([
          {
            groupId: 'g',
            groupName: 'U10 Yüzme',
            sessionKey: `${today.date}T00:00`,
            date: today.date,
            start: '00:00',
            end: '23:59',
            status: 'Present',
          },
        ]),
        dues: [due(), due({ period: '2026-10', remaining: { amount: '100.00', currency: 'TRY' } })],
      }),
    );
    renderApp('/');

    const card = await screen.findByRole('article', { name: 'Ada Deneme' });
    expect(await within(card).findByText('Şu anda: 00:00–23:59')).toBeInTheDocument();
    expect(within(card).getByText('U10 Yüzme · Kapalı Havuz')).toBeInTheDocument();
    expect(await within(card).findByText('Bugün antrenmana katıldı')).toBeInTheDocument();
    expect(await within(card).findByTestId('owed-total')).toHaveTextContent('₺1.000,00');
    expect(within(card).getByRole('link', { name: 'Ada Deneme: ayrıntılar' })).toHaveAttribute(
      'href',
      `/cocuk/${CHILD_ID}`,
    );
  });

  it('says when nothing is owed and there is no session today', async () => {
    signIn();
    fakeApi(childRoutes({ schedule: [group({ slots: [] })] }));
    renderApp('/');

    const card = await screen.findByRole('article', { name: 'Ada Deneme' });
    expect(await within(card).findByText(text.children.nothingOwed)).toBeInTheDocument();
    expect(within(card).getByText(text.children.noTraining)).toBeInTheDocument();
    expect(within(card).queryByText(text.children.today)).not.toBeInTheDocument();
  });

  it('explains an account without children', async () => {
    signIn();
    fakeApi(childRoutes({ children: [] }));
    renderApp('/');

    expect(await screen.findByText(text.children.empty)).toBeInTheDocument();
  });

  it('shows a failed load with a retry that works', async () => {
    signIn();
    const api = fakeApi(childRoutes({}));
    api.on('GET /api/v1/parent/children', () => problem(500, 'Unexpected.Error'));
    renderApp('/');

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Beklenmeyen bir hata oluştu.');
    api.on('GET /api/v1/parent/children', () => json([child()]));
    await userEvent.click(within(alert).getByRole('button', { name: text.app.retry }));

    expect(await screen.findByRole('article', { name: 'Ada Deneme' })).toBeInTheDocument();
  });
});

describe('child detail', () => {
  it('lists dues with the list fee, the sibling discount and the program, attendance, payments, announcements', async () => {
    signIn();
    fakeApi(
      childRoutes({
        dues: [due()],
        payments: {
          items: [
            {
              id: 'p1',
              enrollmentId: ENROLLMENT_ID,
              groupId: 'g',
              groupName: 'U10 Yüzme',
              period: '2026-10',
              amount: { amount: '900.00', currency: 'TRY' },
              method: 'Online',
              paidOn: '2026-10-05',
              status: 'Recorded',
              refundedAt: null,
            },
            {
              id: 'p2',
              enrollmentId: ENROLLMENT_ID,
              groupId: 'g',
              groupName: 'U10 Yüzme',
              period: '2026-09',
              amount: { amount: '800.00', currency: 'TRY' },
              method: 'Online',
              paidOn: '2026-09-05',
              status: 'Refunded',
              refundedAt: '2026-11-09T07:00:00Z',
            },
          ],
          page: 1,
          pageSize: 10,
          totalCount: 2,
          totalPages: 1,
        },
        announcements: {
          items: [
            {
              id: 'a1',
              groupId: 'g',
              groupName: 'U10 Yüzme',
              title: 'Havuz bakımı',
              body: 'Cuma antrenmanı yapılmayacak.',
              publishedAt: '2026-11-10T07:00:00Z',
            },
          ],
          page: 1,
          pageSize: 10,
          totalCount: 1,
          totalPages: 1,
        },
      }),
    );
    renderApp(`/cocuk/${CHILD_ID}`);

    expect(await screen.findByRole('heading', { level: 1, name: 'Ada Deneme' })).toBeInTheDocument();
    const dues = screen.getByRole('region', { name: text.dues.title });
    const row = await within(dues).findByTestId('due-row');
    expect(within(row).getByText('Kasım 2026')).toBeInTheDocument();
    expect(within(row).getByTestId('list-fee')).toHaveTextContent('₺1.000,00');
    expect(within(row).getByTestId('discount-hint')).toHaveTextContent('Kardeş indirimi %10');
    expect(within(dues).getByTestId('dues-total')).toHaveTextContent('Toplam ödenmemiş: ₺900,00');

    const program = screen.getByRole('region', { name: text.schedule.title });
    expect(await within(program).findByText('Çarşamba')).toBeInTheDocument();
    expect(within(program).getByText(/Antrenör: Can Koç/)).toBeInTheDocument();

    const payments = screen.getByRole('region', { name: text.payments.title });
    expect(await within(payments).findByText('Ekim 2026')).toBeInTheDocument();
    const paid = within(payments).getByTestId('payment-p1');
    expect(paid).toHaveTextContent('Online');
    expect(paid).not.toHaveTextContent(text.payments.refunded);
    // P3-13: a refunded payment stays in the history, marked, with when the money went back to the card.
    const refunded = within(payments).getByTestId('payment-p2');
    expect(refunded).toHaveTextContent(text.payments.refunded);
    expect(refunded).toHaveTextContent('Karta iade: 09.11.2026 10:00');
    expect(within(refunded).getByText('₺800,00', { exact: false })).toHaveClass('line-through');

    const announcements = screen.getByRole('region', { name: text.announcements.title });
    expect(await within(announcements).findByText('Havuz bakımı')).toBeInTheDocument();
    expect(within(announcements).getByText('U10 Yüzme · 10.11.2026 10:00')).toBeInTheDocument();
  });

  it('starts an online payment with an idempotency key and goes to the checkout page', async () => {
    signIn();
    const assign = vi.spyOn(browser, 'assign').mockImplementation(() => undefined);
    const api = fakeApi({
      ...childRoutes({ dues: [due()] }),
      [PAY]: () => json(onlinePayment('/sandbox-odeme?token=tok'), 201),
    });
    renderApp(`/cocuk/${CHILD_ID}`);

    await userEvent.click(
      await screen.findByRole('button', { name: 'Kasım 2026 aidatını online öde (₺900,00)' }),
    );

    await vi.waitFor(() => {
      expect(assign).toHaveBeenCalledWith(`${window.location.origin}/sandbox-odeme?token=tok`);
    });
    const [request] = api.callsTo(PAY);
    expect(request!.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(await request!.json()).toEqual({ enrollmentId: ENROLLMENT_ID, period: '2026-11' });
  });

  it('shows why a payment could not start and retries with the same key', async () => {
    signIn();
    const assign = vi.spyOn(browser, 'assign').mockImplementation(() => undefined);
    const api = fakeApi({
      ...childRoutes({ dues: [due()] }),
      [PAY]: () => problem(409, 'OnlinePayment.CheckoutInProgress'),
    });
    renderApp(`/cocuk/${CHILD_ID}`);
    const pay = await screen.findByRole('button', { name: /aidatını online öde/ });

    await userEvent.click(pay);
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Bu aidat için başlatılmış bir ödeme zaten var.',
    );

    api.on(PAY, () => json(onlinePayment('javascript:alert(1)'), 200));
    await userEvent.click(pay);
    expect(await screen.findByText(text.dues.badCheckout)).toBeInTheDocument();

    const keys = api.callsTo(PAY).map((r) => r.headers.get('Idempotency-Key'));
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
    expect(assign).not.toHaveBeenCalled();
  });

  it('hides online payment while the school has it switched off (P4-10)', async () => {
    signIn();
    const api = fakeApi(childRoutes({ dues: [due()], features: ['reports'] }));
    renderApp(`/cocuk/${CHILD_ID}`);

    expect(await screen.findByTestId('pay-unavailable')).toHaveTextContent(text.dues.payUnavailable);
    expect(screen.getAllByTestId('due-row')).toHaveLength(1);
    expect(screen.queryByRole('button', { name: /aidatını online öde/ })).not.toBeInTheDocument();
    expect(api.callsTo(PAY)).toHaveLength(0);
  });

  it('reads as not found for a child that is not linked to the account', async () => {
    signIn();
    fakeApi(childRoutes({}));
    renderApp('/cocuk/0190a1b2-0000-7000-8000-0000000000ff');

    expect(await screen.findByText(text.child.notFound)).toBeInTheDocument();
  });
});
