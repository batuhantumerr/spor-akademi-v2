import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { GroupDto, OutstandingDueDto, PaymentDto } from '../../api/models';
import { AuthService } from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';
import { displayMoney, parseMoneyInput, sumAmounts, toKurus } from '../../core/money/money';
import { SchoolClock } from '../../core/time/school-clock';
import { ToastService } from '../../shared/ui/toast.service';
import {
  CancelPaymentDialog,
  PAYMENT_SEARCH_DEBOUNCE_MS,
  RecordPaymentDialog,
  RefundPaymentDialog,
} from './billing-dialogs';
import { DuesPage } from './dues.page';
import { GroupFeesComponent } from './group-fees.component';
import { PaymentsPage } from './payments.page';

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await wait(0);
  fixture.detectChanges();
}

const tl = (amount: string) => ({ amount, currency: 'TRY' });

describe('money as text', () => {
  it.each([
    ['1250', '1250.00'],
    ['1.250', '1250.00'],
    ['1.250,5', '1250.50'],
    ['1250,50', '1250.50'],
    ['1250.5', '1250.50'],
    ['₺ 1.250,00', '1250.00'],
    ['0,99', '0.99'],
    ['0.5', '0.50'],
    ['12.500.000', '12500000.00'],
    ['9999999999,99', '9999999999.99'],
  ])('reads %s as %s', (input, expected) => {
    expect(parseMoneyInput(input)).toBe(expected);
  });

  it.each([
    '',
    'abc',
    '12,345',
    '1.25.0',
    '-5',
    '1,2,3',
    '12345678901',
    '0.500',
    '0.250',
    '00.500',
    '0.500,5',
  ])('rejects %s', (input) => {
    expect(parseMoneyInput(input)).toBeNull();
  });

  it('handles signs and refuses malformed API text', () => {
    expect(toKurus('-1.25')).toBe(-125n);
    expect(sumAmounts(['10.00', '-2.50'])).toBe('7.50');
    expect(() => toKurus('1,25')).toThrow();
    expect(displayMoney('abc')).toBe('—');
    expect(displayMoney('-12.5')).toBe('-₺12,50');
  });

  it('shows ₺1.250,00 from the text without floating point', () => {
    expect(displayMoney('1250.00')).toBe('₺1.250,00');
    expect(displayMoney('0.1')).toBe('₺0,10');
    expect(displayMoney('9999999999.99')).toBe('₺9.999.999.999,99');
    expect(displayMoney(tl('35.05'))).toBe('₺35,05');
  });

  it('adds exactly (0.10 + 0.20 = 0.30)', () => {
    expect(sumAmounts(['0.10', '0.20'])).toBe('0.30');
    expect(sumAmounts(['1250.00', '1250.00', '0.01'])).toBe('2500.01');
    expect(sumAmounts([])).toBe('0.00');
  });
});

const group: GroupDto = {
  id: 'g1',
  name: 'U12 Mavi',
  sport: { id: 'sp1', name: 'Futbol' },
  facility: { id: 'f1', name: 'Ana saha' },
  coach: null,
  birthYearRange: { from: 2014, to: 2015 },
  capacity: 16,
  status: 'Active',
  archivedOn: null,
  slots: [],
  version: 3,
};

function providers(extra: unknown[] = []) {
  return [
    provideRouter([]),
    provideHttpClient(),
    provideHttpClientTesting(),
    { provide: AuthService, useValue: { user: signal(null) } },
    ...extra,
  ];
}

describe('GroupFeesComponent', () => {
  let fixture: ComponentFixture<GroupFeesComponent>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const toast = { success: vi.fn(), error: vi.fn() };

  beforeEach(async () => {
    toast.success.mockReset();
    TestBed.configureTestingModule({
      imports: [GroupFeesComponent],
      providers: providers([{ provide: ToastService, useValue: toast }]),
    });
    fixture = TestBed.createComponent(GroupFeesComponent);
    fixture.componentRef.setInput('group', group);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    await settle(fixture);
  });

  afterEach(() => backend.verify());

  const fees = () =>
    backend.expectOne((r) => r.url === '/api/v1/groups/g1/fees' && r.method === 'GET');
  async function type(id: string, value: string): Promise<void> {
    const el = root.querySelector(`#${id}`) as HTMLInputElement;
    el.value = value;
    el.dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  it('marks the fee in force this month as current and later ones as upcoming', async () => {
    const thisMonth = TestBed.inject(SchoolClock).today().slice(0, 7);
    const [y, m] = thisMonth.split('-').map(Number);
    const next = `${m === 12 ? y + 1 : y}-${String(m === 12 ? 1 : m + 1).padStart(2, '0')}`;
    fees().flush([
      { effectiveFrom: '2020-01', amount: tl('900.00') },
      { effectiveFrom: next, amount: tl('1500.00') },
    ]);
    await settle(fixture);
    const items = root.querySelectorAll('li');
    expect(items[0].textContent).toContain('₺1.500,00');
    expect(items[0].textContent).toContain(ui.billing.upcomingFee);
    expect(items[0].textContent).not.toContain(ui.billing.currentFee);
    expect(items[1].textContent).toContain(ui.billing.currentFee);
  });

  it('previews the amount politely and accepts a zero fee', async () => {
    fees().flush([]);
    await settle(fixture);
    await type('fee-amount', '0');
    const preview = root.querySelector('#fee-amount-hint + [aria-live="polite"]');
    expect(preview?.textContent).toContain('₺0,00');
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
    const post = backend.expectOne((r) => r.method === 'POST');
    expect((post.request.body as { amount: unknown }).amount).toEqual(tl('0.00'));
    post.flush({});
    await settle(fixture);
    fees().flush([]);
    await settle(fixture);
  });

  it('focuses the first invalid field on submit', async () => {
    document.body.appendChild(root);
    fees().flush([]);
    await settle(fixture);
    await type('fee-amount', 'abc');
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
    expect(document.activeElement).toBe(root.querySelector('#fee-amount'));
    root.remove();
  });

  it('lists the fee history newest first with the current one marked', async () => {
    fees().flush([
      { effectiveFrom: '2026-09', amount: tl('1000.00') },
      { effectiveFrom: '2026-11', amount: tl('1250.00') },
    ]);
    await settle(fixture);
    const items = root.querySelectorAll('li');
    expect(items[0].textContent).toContain('Kasım 2026');
    expect(items[0].textContent).toContain('₺1.250,00');
    expect(items[0].textContent).toContain(ui.billing.currentFee);
    expect(items[1].textContent).toContain('₺1.000,00');
  });

  it('defines a fee from this month with the amount as text', async () => {
    fees().flush([]);
    await settle(fixture);
    const month = TestBed.inject(SchoolClock).today().slice(0, 7);
    expect((root.querySelector('#fee-from') as HTMLInputElement).value).toBe(month);
    await type('fee-amount', '1.250,5');
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);

    const post = backend.expectOne((r) => r.method === 'POST');
    expect(post.request.body).toEqual({ effectiveFrom: month, amount: tl('1250.50') });
    post.flush({});
    await settle(fixture);
    fees().flush([{ effectiveFrom: month, amount: tl('1250.50') }]);
    await settle(fixture);
    expect(toast.success).toHaveBeenCalledWith(ui.billing.feeDefined);
  });

  it('rejects a malformed amount and a past month on the client, and maps API errors', async () => {
    fees().flush([]);
    await settle(fixture);
    await type('fee-amount', '12,345');
    await type('fee-from', '2000-01');
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
    backend.expectNone((r) => r.method === 'POST');
    expect(root.querySelector('#fee-amount-error')?.textContent).toContain(
      errorMessages['Money.Invalid'],
    );
    expect(root.querySelector('#fee-from-error')?.textContent).toContain(
      errorMessages['Fee.PeriodInPast'],
    );

    const month = TestBed.inject(SchoolClock).today().slice(0, 7);
    await type('fee-from', month);
    await type('fee-amount', '900');
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
    backend
      .expectOne((r) => r.method === 'POST')
      .flush({ code: 'Fee.AlreadyDefined' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    expect(root.querySelector('#fee-from-error')?.textContent).toContain(
      errorMessages['Fee.AlreadyDefined'],
    );
  });
});

describe('billing dialogs', () => {
  let backend: HttpTestingController;
  let root: HTMLElement;
  let fixture: ComponentFixture<unknown>;
  const close = vi.fn();

  async function open<T>(component: Type<T>, data: unknown): Promise<void> {
    close.mockReset();
    TestBed.configureTestingModule({
      imports: [component],
      providers: providers([
        { provide: DIALOG_DATA, useValue: data },
        { provide: DialogRef, useValue: { close } },
      ]),
    });
    fixture = TestBed.createComponent(component);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    document.body.appendChild(root);
    await settle(fixture);
  }

  afterEach(() => {
    root?.remove();
    backend.verify();
  });

  async function type(id: string, value: string): Promise<void> {
    const el = root.querySelector(`#${id}`) as HTMLInputElement;
    el.value = value;
    el.dispatchEvent(new Event('input'));
    await settle(fixture);
  }
  const submit = async () => {
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
  };

  it('records a payment for a found student and enrollment, sending money as text', async () => {
    await open(RecordPaymentDialog, {});
    await type('pay-search', 'ayşe');
    await wait(PAYMENT_SEARCH_DEBOUNCE_MS + 50);
    backend
      .expectOne((r) => r.url === '/api/v1/students')
      .flush({
        items: [
          {
            id: 'st1',
            firstName: 'Ayşe',
            lastName: 'Yılmaz',
            birthDate: '2014-05-02',
            gender: 'Female',
            status: 'Active',
            leftAt: null,
          },
        ],
        page: 1,
        pageSize: 10,
        totalCount: 1,
        totalPages: 1,
      });
    await settle(fixture);
    (root.querySelector('button[aria-pressed]') as HTMLButtonElement).click();
    await settle(fixture);
    backend.expectOne('/api/v1/students/st1/enrollments').flush([
      {
        id: 'e1',
        studentId: 'st1',
        studentFirstName: 'Ayşe',
        studentLastName: 'Yılmaz',
        groupId: 'g1',
        groupName: 'U12 Mavi',
        startDate: '2026-09-01',
        endDate: null,
        status: 'Active',
      },
    ]);
    await settle(fixture);

    await type('pay-period', '2026-11');
    await type('pay-amount', '1.250,00');
    expect(root.querySelector('#pay-amount-hint')?.textContent).toContain('₺1.250,00');
    const methodSelect = root.querySelector('#pay-method') as HTMLSelectElement;
    methodSelect.value = 'BankTransfer';
    methodSelect.dispatchEvent(new Event('change'));
    await submit();

    const post = backend.expectOne('/api/v1/payments');
    expect(post.request.body).toEqual({
      enrollmentId: 'e1',
      period: '2026-11',
      amount: tl('1250.00'),
      method: 'BankTransfer',
      paidOn: TestBed.inject(SchoolClock).today(),
    });
    post.flush({ id: 'p1' });
    await settle(fixture);
    expect(close).toHaveBeenCalled();
  });

  it('prefills enrollment, period and the remaining amount from a due', async () => {
    await open(RecordPaymentDialog, {
      prefill: {
        enrollmentId: 'e1',
        label: 'Ayşe Yılmaz · U12 Mavi',
        period: '2026-11',
        amount: '350.50',
      },
    });
    expect(root.textContent).toContain('Ayşe Yılmaz · U12 Mavi');
    expect((root.querySelector('#pay-amount') as HTMLInputElement).value).toBe('350,50');
    expect((root.querySelector('#pay-period') as HTMLInputElement).readOnly).toBe(true);
    await submit();
    const post = backend.expectOne('/api/v1/payments');
    expect(post.request.body).toEqual(
      expect.objectContaining({ enrollmentId: 'e1', period: '2026-11', amount: tl('350.50') }),
    );
    post.flush({ id: 'p1' });
    await settle(fixture);
  });

  it('maps fee and period problems to the period, amount problems to the amount', async () => {
    await open(RecordPaymentDialog, {
      prefill: { enrollmentId: 'e1', label: 'x', period: '2026-11', amount: '100.00' },
    });
    await submit();
    backend
      .expectOne('/api/v1/payments')
      .flush({ code: 'Payment.FeeNotDefined' }, { status: 422, statusText: 'Unprocessable' });
    await settle(fixture);
    expect(root.querySelector('#pay-period-error')?.textContent).toContain(
      errorMessages['Payment.FeeNotDefined'],
    );

    await type('pay-amount', '0');
    await submit();
    backend.expectNone('/api/v1/payments');
    expect(root.querySelector('#pay-amount-error')?.textContent).toContain(
      errorMessages['Payment.InvalidAmount'],
    );
  });

  it('shows a field error for a hidden field (prefilled enrollment) in the banner', async () => {
    await open(RecordPaymentDialog, {
      prefill: { enrollmentId: 'e1', label: 'x', period: '2026-11', amount: '100.00' },
    });
    await submit();
    backend
      .expectOne('/api/v1/payments')
      .flush(
        { code: 'Validation.Failed', errors: { enrollmentId: ['Field.Invalid'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle(fixture);
    await settle(fixture);
    expect(root.querySelector('#pay-error')?.textContent).toContain(
      errorMessages['Validation.Failed'],
    );
    expect(document.activeElement).toBe(root.querySelector('#pay-error'));
  });

  it('gives paid-on its own message and bounds the year', async () => {
    await open(RecordPaymentDialog, {
      prefill: { enrollmentId: 'e1', label: 'x', period: '2026-11', amount: '100.00' },
    });
    await type('pay-paid-on', '1999-12-31');
    await submit();
    backend.expectNone('/api/v1/payments');
    expect(root.querySelector('#pay-paid-on-error')?.textContent).toContain(
      ui.billing.paidOnInvalid,
    );
  });

  it('focuses the reason when empty and the alert on a server error; closes when already cancelled', async () => {
    const payment = {
      id: 'p1',
      studentFirstName: 'Ayşe',
      studentLastName: 'Yılmaz',
      period: '2026-11',
      amount: tl('1250.00'),
    } as PaymentDto;
    await open(CancelPaymentDialog, { payment });
    await submit();
    await settle(fixture);
    expect(document.activeElement).toBe(root.querySelector('#cancel-reason'));

    await type('cancel-reason', 'Yanlış');
    await submit();
    backend.expectOne('/api/v1/payments/p1/cancel').flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    await settle(fixture);
    const alert = root.querySelector('#cancel-error') as HTMLElement;
    expect(alert.getAttribute('tabindex')).toBe('-1');
    expect(document.activeElement).toBe(alert);

    await submit();
    backend
      .expectOne('/api/v1/payments/p1/cancel')
      .flush({ code: 'Payment.AlreadyCancelled' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    expect(close).toHaveBeenCalledWith('alreadyCancelled');
  });

  it('requires a cancel reason and sends it', async () => {
    const payment = {
      id: 'p1',
      studentFirstName: 'Ayşe',
      studentLastName: 'Yılmaz',
      period: '2026-11',
      amount: tl('1250.00'),
    } as PaymentDto;
    await open(CancelPaymentDialog, { payment });
    expect(root.textContent).toContain('Ayşe Yılmaz · Kasım 2026 · ₺1.250,00');
    // DEC-367: the reason is never erased, so the dialog warns against personal data.
    const reasonField = root.querySelector('#cancel-reason') as HTMLTextAreaElement;
    expect(reasonField.getAttribute('aria-describedby')).toBe('cancel-reason-hint');
    expect(root.querySelector('#cancel-reason-hint')?.textContent).toContain(
      ui.billing.cancelReasonHint,
    );
    await submit();
    backend.expectNone('/api/v1/payments/p1/cancel');
    expect(reasonField.getAttribute('aria-describedby')).toBe(
      'cancel-reason-hint cancel-reason-error',
    );
    expect(root.querySelector('#cancel-reason-error')?.textContent).toContain(
      errorMessages['Payment.CancelReasonRequired'],
    );

    await type('cancel-reason', '  Yanlış dönem  ');
    await submit();
    const post = backend.expectOne('/api/v1/payments/p1/cancel');
    expect(post.request.body).toEqual({ reason: 'Yanlış dönem' });
    post.flush({ ...payment, status: 'Cancelled' });
    await settle(fixture);
    expect(close).toHaveBeenCalled();
  });

  const refundData = {
    payment: {
      id: 'p1',
      studentFirstName: 'Ayşe',
      studentLastName: 'Yılmaz',
      period: '2026-11',
      amount: tl('1250.00'),
    },
  };

  it('requires a refund reason, warns about personal data and that the due reopens', async () => {
    await open(RefundPaymentDialog, refundData);
    expect(root.textContent).toContain('Ayşe Yılmaz · Kasım 2026 · ₺1.250,00');
    expect(root.querySelector('#refund-effect')?.textContent).toContain(ui.billing.refundReopens);
    expect(root.querySelector('#refund-reason-hint')?.textContent).toContain(
      ui.billing.refundReasonHint,
    );
    await submit();
    backend.expectNone('/api/v1/payments/p1/refund');
    expect(root.querySelector('#refund-reason-error')?.textContent).toContain(
      errorMessages['Refund.ReasonRequired'],
    );
    expect(document.activeElement).toBe(root.querySelector('#refund-reason'));
  });

  it('says the due stays paid when the live overpayment covers the amount', async () => {
    await open(RefundPaymentDialog, { ...refundData, overpaidBy: tl('1250.00') });
    expect(root.querySelector('#refund-effect')?.textContent).toContain(ui.billing.refundOverpaid);
  });

  it('says how much of the due reopens when the overpayment is smaller than the amount', async () => {
    await open(RefundPaymentDialog, { ...refundData, overpaidBy: tl('250.00') });
    const effect = root.querySelector('#refund-effect')?.textContent ?? '';
    expect(effect).toContain('₺250,00');
    expect(effect).toContain('₺1.000,00');
    expect(effect).not.toContain(ui.billing.refundOverpaid);
  });

  it('says the due reopens without an overpayment', async () => {
    await open(RefundPaymentDialog, { ...refundData, overpaidBy: null });
    expect(root.querySelector('#refund-effect')?.textContent).toContain(ui.billing.refundReopens);
  });

  it('sends the same Idempotency-Key when the same refund is retried and closes when refunded', async () => {
    await open(RefundPaymentDialog, refundData);
    await type('refund-reason', '  Kayıt erken bitti  ');
    await submit();
    const first = backend.expectOne('/api/v1/payments/p1/refund');
    expect(first.request.body).toEqual({ reason: 'Kayıt erken bitti' });
    const key = first.request.headers.get('Idempotency-Key');
    expect(key).toMatch(/^[A-Za-z0-9._:-]{8,100}$/);
    first.flush(null, { status: 0, statusText: '' });
    await settle(fixture);
    await settle(fixture);
    expect(document.activeElement).toBe(root.querySelector('#refund-error'));

    await submit();
    const retry = backend.expectOne('/api/v1/payments/p1/refund');
    expect(retry.request.headers.get('Idempotency-Key')).toBe(key);
    retry.flush({ id: 'p1', status: 'Refunded', refund: { status: 'Succeeded' } });
    await settle(fixture);
    expect(close).toHaveBeenCalledWith(expect.objectContaining({ status: 'Refunded' }));
  });

  it('closes as pending when the provider answer is unknown (202)', async () => {
    await open(RefundPaymentDialog, refundData);
    await type('refund-reason', 'Kayıt erken bitti');
    await submit();
    backend
      .expectOne('/api/v1/payments/p1/refund')
      .flush(
        { id: 'p1', status: 'Recorded', refund: { status: 'Pending' } },
        { status: 202, statusText: 'Accepted' },
      );
    await settle(fixture);
    expect(close).toHaveBeenCalledWith('refundPending');
  });

  it('shows a declined refund and retries it with a new key; closes when someone else refunded', async () => {
    await open(RefundPaymentDialog, refundData);
    await type('refund-reason', 'Kayıt erken bitti');
    await submit();
    const declined = backend.expectOne('/api/v1/payments/p1/refund');
    const key = declined.request.headers.get('Idempotency-Key');
    declined.flush(
      { code: 'Refund.ProviderDeclined' },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    await settle(fixture);
    expect(root.querySelector('#refund-error')?.textContent).toContain(
      errorMessages['Refund.ProviderDeclined'],
    );
    expect(close).not.toHaveBeenCalled();

    await submit();
    const retry = backend.expectOne('/api/v1/payments/p1/refund');
    expect(retry.request.headers.get('Idempotency-Key')).not.toBe(key);
    retry.flush({ code: 'Refund.AlreadyRefunded' }, { status: 422, statusText: 'Unprocessable' });
    await settle(fixture);
    expect(close).toHaveBeenCalledWith('refundAlreadyHandled');
  });
});

describe('PaymentsPage', () => {
  it('lists payments with formatted money and filters by status', async () => {
    const open = vi.fn();
    TestBed.configureTestingModule({
      imports: [PaymentsPage],
      providers: providers([
        { provide: Dialog, useValue: { open } },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
      ]),
    });
    const fixture = TestBed.createComponent(PaymentsPage);
    const backend = TestBed.inject(HttpTestingController);
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/groups')
      .flush({ items: [group], page: 1, pageSize: 100, totalCount: 1, totalPages: 1 });
    const payment: PaymentDto = {
      id: 'p1',
      enrollmentId: 'e1',
      studentId: 'st1',
      studentFirstName: 'Ayşe',
      studentLastName: 'Yılmaz',
      groupId: 'g1',
      groupName: 'U12 Mavi',
      period: '2026-11',
      amount: tl('1250.00'),
      method: 'Cash',
      paidOn: '2026-11-05',
      status: 'Cancelled',
      cancelReason: 'Yanlış dönem',
      cancelledBy: 'u1',
      cancelledAt: '2026-11-06T10:00:00Z',
      onlinePaymentId: null,
      refund: null,
      overpaidBy: null,
    };
    backend
      .expectOne((r) => r.url === '/api/v1/payments')
      .flush({ items: [payment], page: 1, pageSize: 25, totalCount: 1, totalPages: 1 });
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('₺1.250,00');
    expect(root.textContent).toContain('Kasım 2026');
    expect(root.textContent).toContain('Nakit');
    expect(root.textContent).toContain('İptal: Yanlış dönem');
    // A cancelled payment cannot be cancelled again.
    expect(
      [...root.querySelectorAll('button')].some((b) => b.textContent?.trim() === ui.billing.cancel),
    ).toBe(false);

    const status = root.querySelector('#pay-filter-status') as HTMLSelectElement;
    status.value = 'Recorded';
    status.dispatchEvent(new Event('change'));
    await settle(fixture);
    const filtered = backend.expectOne((r) => r.url === '/api/v1/payments');
    expect(filtered.request.params.get('status')).toBe('Recorded');
    filtered.flush({ items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 });
    await settle(fixture);
    expect(root.textContent).toContain(ui.billing.noPayments);
    backend.verify();
  });

  it('offers a refund only for recorded online payments and shows refund states (P3-13)', async () => {
    const open = vi.fn().mockReturnValue({ closed: of(refundedPayment) });
    const toast = { success: vi.fn(), error: vi.fn() };
    TestBed.configureTestingModule({
      imports: [PaymentsPage],
      providers: providers([
        { provide: Dialog, useValue: { open } },
        { provide: ToastService, useValue: toast },
      ]),
    });
    const fixture = TestBed.createComponent(PaymentsPage);
    const backend = TestBed.inject(HttpTestingController);
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/groups')
      .flush({ items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 });
    const refund = {
      id: 'r1',
      reason: 'Kayıt erken bitti',
      requestedBy: 'u1',
      requestedAt: '2026-11-10T10:00:00Z',
      attempts: 1,
      needsReview: false,
    };
    const items: PaymentDto[] = [
      onlinePayment('cash', { method: 'Cash', onlinePaymentId: null }),
      onlinePayment('online'),
      onlinePayment('failed', {
        refund: {
          ...refund,
          status: 'Failed',
          completedAt: '2026-11-10T10:00:01Z',
          failureCode: 'FakeRefundDeclined',
        },
      }),
      onlinePayment('pending', {
        refund: { ...refund, status: 'Pending', completedAt: null, failureCode: null },
      }),
      { ...refundedPayment, id: 'done' },
    ];
    backend
      .expectOne((r) => r.url === '/api/v1/payments')
      .flush({ items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 });
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    const row = (id: string) => root.querySelector(`[data-testid="payment-${id}"]`) as HTMLElement;
    const buttons = (id: string) =>
      [...row(id).querySelectorAll('button')].map((b) => b.textContent?.trim());
    expect(buttons('cash')).toEqual([ui.billing.cancel]);
    expect(buttons('online')).toEqual([ui.billing.refund]);
    expect(buttons('failed')).toEqual([ui.billing.refundRetry]);
    expect(row('failed').textContent).toContain(ui.billing.refundStatuses['Failed']);
    expect(row('failed').textContent).toContain('FakeRefundDeclined');
    expect(buttons('pending')).toEqual([ui.billing.refundCheck]);
    expect(row('pending').textContent).toContain(ui.billing.refundStatuses['Pending']);
    expect(buttons('done')).toEqual([]);
    expect(row('done').querySelector('[data-testid="status-Refunded"]')?.textContent).toContain(
      ui.billing.statuses['Refunded'],
    );
    expect(row('done').textContent).toContain('İade gerekçesi: Kayıt erken bitti');

    (row('online').querySelector('button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(open.mock.calls[0][1].data.payment.id).toBe('online');
    expect(open.mock.calls[0][1].data.overpaidBy).toBeNull();
    expect(toast.success).toHaveBeenCalledWith(ui.billing.refunded);
    backend
      .expectOne((r) => r.url === '/api/v1/payments')
      .flush({ items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 });

    const status = root.querySelector('#pay-filter-status') as HTMLSelectElement;
    expect([...status.options].map((o) => o.value)).toContain('Refunded');
    backend.verify();
  });
});

describe('PaymentsPage refund recovery', () => {
  it('marks overpayments, flags refunds needing review and checks them at the provider', async () => {
    const toast = { success: vi.fn(), error: vi.fn() };
    TestBed.configureTestingModule({
      imports: [PaymentsPage],
      providers: providers([
        { provide: Dialog, useValue: { open: vi.fn() } },
        { provide: ToastService, useValue: toast },
      ]),
    });
    const fixture = TestBed.createComponent(PaymentsPage);
    const backend = TestBed.inject(HttpTestingController);
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/groups')
      .flush({ items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 });
    const items: PaymentDto[] = [
      onlinePayment('over', { overpaidBy: tl('1250.00') }),
      onlinePayment('review', {
        refund: {
          id: 'r1',
          status: 'Pending',
          reason: 'Kayıt erken bitti',
          requestedBy: 'u1',
          requestedAt: '2026-11-10T10:00:00Z',
          completedAt: null,
          failureCode: null,
          attempts: 2,
          needsReview: true,
        },
      }),
    ];
    backend
      .expectOne((r) => r.url === '/api/v1/payments')
      .flush({ items, page: 1, pageSize: 25, totalCount: items.length, totalPages: 1 });
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    const row = (id: string) =>
      root.querySelector('[data-testid="payment-' + id + '"]') as HTMLElement;
    expect(row('over').querySelector('[data-testid="overpaid"]')?.textContent).toContain(
      ui.billing.overpaid,
    );
    expect(row('over').textContent).toContain('₺1.250,00 fazla ödenmiş');
    expect(
      row('review').querySelector('[data-testid="refund-needs-review"]')?.textContent,
    ).toContain(ui.billing.refundNeedsReview);
    expect(row('review').querySelector('[data-testid="overpaid"]')).toBeNull();

    (row('review').querySelector('[data-testid="refund-check"]') as HTMLButtonElement).click();
    await settle(fixture);
    const check = backend.expectOne('/api/v1/payments/review/refund/check');
    expect(check.request.method).toBe('POST');
    check.flush({ code: 'Refund.ProviderDeclined' }, { status: 422, statusText: 'Unprocessable' });
    await settle(fixture);
    expect(toast.error).toHaveBeenCalledWith(ui.billing.refundCheckNotRefunded);
    backend
      .expectOne((r) => r.url === '/api/v1/payments')
      .flush({ items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 });
    backend.verify();
  });
});

function onlinePayment(id: string, overrides: Partial<PaymentDto> = {}): PaymentDto {
  return {
    id,
    enrollmentId: 'e1',
    studentId: 'st1',
    studentFirstName: 'Ayşe',
    studentLastName: 'Yılmaz',
    groupId: 'g1',
    groupName: 'U12 Mavi',
    period: '2026-11',
    amount: tl('1250.00'),
    method: 'Online',
    paidOn: '2026-11-05',
    status: 'Recorded',
    cancelReason: null,
    cancelledBy: null,
    cancelledAt: null,
    onlinePaymentId: 'op1',
    refund: null,
    overpaidBy: null,
    ...overrides,
  };
}

const refundedPayment: PaymentDto = onlinePayment('refunded', {
  status: 'Refunded',
  refund: {
    id: 'r9',
    status: 'Succeeded',
    reason: 'Kayıt erken bitti',
    requestedBy: 'u1',
    requestedAt: '2026-11-10T10:00:00Z',
    completedAt: '2026-11-10T10:00:01Z',
    failureCode: null,
    attempts: 1,
    needsReview: false,
  },
});

describe('DuesPage', () => {
  const due = (id: string, fee: string, paid: string, remaining: string): OutstandingDueDto => ({
    enrollmentId: id,
    studentId: `st-${id}`,
    studentFirstName: 'Öğrenci',
    studentLastName: id,
    groupId: 'g1',
    groupName: 'U12 Mavi',
    period: '2026-11',
    fee: tl(fee),
    paid: tl(paid),
    remaining: tl(remaining),
    listFee: tl(fee),
    discount: tl('0.00'),
    adjustment: 'None',
    discountPercent: 0,
  });

  it('sums every page exactly and opens a prefilled payment', async () => {
    const open = vi.fn();
    const toast = { success: vi.fn(), error: vi.fn() };
    TestBed.configureTestingModule({
      imports: [DuesPage],
      providers: providers([
        { provide: Dialog, useValue: { open } },
        { provide: ToastService, useValue: toast },
      ]),
    });
    const fixture = TestBed.createComponent(DuesPage);
    const backend = TestBed.inject(HttpTestingController);
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/groups')
      .flush({ items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 });

    const dues = () => backend.match((r) => r.url === '/api/v1/dues/outstanding');
    let [listReq, totalsReq] = dues();
    if (listReq.request.params.get('pageSize') === '100') {
      [listReq, totalsReq] = [totalsReq, listReq];
    }
    const first = [due('a', '1250.00', '0.10', '1249.90'), due('b', '1250.00', '0.20', '1249.80')];
    listReq.flush({ items: first, page: 1, pageSize: 25, totalCount: 3, totalPages: 1 });
    totalsReq.flush({ items: first, page: 1, pageSize: 100, totalCount: 3, totalPages: 2 });
    await settle(fixture);
    // The second page of the totals walk.
    const page2 = backend.expectOne(
      (r) => r.url === '/api/v1/dues/outstanding' && r.params.get('page') === '2',
    );
    page2.flush({
      items: [due('c', '900.00', '0.00', '900.00')],
      page: 2,
      pageSize: 100,
      totalCount: 3,
      totalPages: 2,
    });
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="total-remaining"]')?.textContent?.trim()).toBe(
      '₺3.399,70',
    );
    expect(root.querySelector('[data-testid="total-paid"]')?.textContent?.trim()).toBe('₺0,30');
    expect(root.querySelector('[data-testid="total-fee"]')?.textContent?.trim()).toBe('₺3.400,00');
    expect(root.textContent).toContain('Toplam (3 kayıt)');

    open.mockReturnValue({ closed: of({ id: 'p1' }) });
    (
      [...root.querySelectorAll('button')].find((b) =>
        b.textContent?.includes(ui.billing.takePayment),
      ) as HTMLButtonElement
    ).click();
    await settle(fixture);
    expect(open).toHaveBeenCalledWith(
      RecordPaymentDialog,
      expect.objectContaining({
        data: {
          prefill: {
            enrollmentId: 'a',
            label: 'Öğrenci a · U12 Mavi',
            period: '2026-11',
            amount: '1249.90',
          },
        },
      }),
    );
    // After the dialog: list and totals reload.
    for (const r of dues()) {
      r.flush({ items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 });
    }
    await settle(fixture);
    expect(toast.success).toHaveBeenCalledWith(ui.billing.paymentRecorded);
    backend.verify();
  });

  it('explains a sibling discount: list fee struck through, owed fee and the percent', async () => {
    TestBed.configureTestingModule({
      imports: [DuesPage],
      providers: providers([
        { provide: Dialog, useValue: { open: vi.fn() } },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
      ]),
    });
    const fixture = TestBed.createComponent(DuesPage);
    const backend = TestBed.inject(HttpTestingController);
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/groups')
      .flush({ items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 });
    const discounted: OutstandingDueDto = {
      ...due('a', '720.00', '0.00', '720.00'),
      listFee: tl('800.00'),
      discount: tl('80.00'),
      adjustment: 'SiblingDiscount',
      discountPercent: 10,
    };
    const full = due('b', '1000.00', '0.00', '1000.00');
    for (const r of backend.match((r) => r.url === '/api/v1/dues/outstanding')) {
      r.flush({ items: [discounted, full], page: 1, pageSize: 25, totalCount: 2, totalPages: 1 });
    }
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    const rows = [...root.querySelectorAll('tbody tr')];
    const feeCell = rows[0].querySelectorAll('td')[2];
    expect(feeCell.querySelector('s')?.textContent?.trim()).toBe('₺800,00');
    expect(feeCell.querySelector('[data-testid="list-fee"] .sr-only')?.textContent).toContain(
      ui.billing.listFee,
    );
    expect(feeCell.textContent).toContain('₺720,00');
    expect(feeCell.querySelector('[data-testid="discount-hint"]')?.textContent?.trim()).toBe(
      'Kardeş indirimi %10',
    );
    // A due without a discount shows only its fee.
    const plainCell = rows[1].querySelectorAll('td')[2];
    expect(plainCell.querySelector('s')).toBeNull();
    expect(plainCell.querySelector('[data-testid="discount-hint"]')).toBeNull();
    // Totals sum what is owed, not the list fees.
    expect(root.querySelector('[data-testid="total-fee"]')?.textContent?.trim()).toBe('₺1.720,00');
    backend.verify();
  });

  it('walks again when the count changes during paging, then shows totals without a count', async () => {
    TestBed.configureTestingModule({
      imports: [DuesPage],
      providers: providers([
        { provide: Dialog, useValue: { open: vi.fn() } },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
      ]),
    });
    const fixture = TestBed.createComponent(DuesPage);
    const backend = TestBed.inject(HttpTestingController);
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/groups')
      .flush({ items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 });
    backend
      .expectOne((r) => r.url === '/api/v1/dues/outstanding' && r.params.get('pageSize') === '25')
      .flush({ items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 });

    // Three walks (first + 2 retries), each claiming 3 rows but returning 2.
    for (let walk = 0; walk < 3; walk++) {
      await settle(fixture);
      backend
        .expectOne(
          (r) => r.url === '/api/v1/dues/outstanding' && r.params.get('pageSize') === '100',
        )
        .flush({
          items: [due('a', '100.00', '0.00', '100.00'), due('b', '50.00', '0.00', '50.00')],
          page: 1,
          pageSize: 100,
          totalCount: 3,
          totalPages: 1,
        });
    }
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="total-remaining"]')?.textContent?.trim()).toBe(
      '₺150,00',
    );
    expect(root.textContent).toContain(ui.billing.totalsNoCount);
    expect(root.textContent).not.toContain('kayıt)');
    backend.verify();
  });
});
