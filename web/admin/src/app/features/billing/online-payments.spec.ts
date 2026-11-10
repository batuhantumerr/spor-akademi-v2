import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';

import { OnlinePaymentDto, OutstandingDueDto } from '../../api/models';
import { AuthService } from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { DuesPage, newIdempotencyKey } from './dues.page';
import { OnlinePaymentsPage, shareableCheckoutUrl } from './online-payments.page';
import { PaymentResultPage, resultStatus } from './payment-result.page';
import { SandboxCheckoutPage, postToCallback } from './sandbox-checkout.page';

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await wait(0);
  fixture.detectChanges();
}

const tl = (amount: string) => ({ amount, currency: 'TRY' });

function providers(query: Record<string, string> = {}, extra: unknown[] = []) {
  return [
    provideRouter([]),
    provideHttpClient(),
    provideHttpClientTesting(),
    { provide: AuthService, useValue: { user: signal(null) } },
    {
      provide: ActivatedRoute,
      useValue: { snapshot: { queryParamMap: convertToParamMap(query) } },
    },
    ...extra,
  ];
}

const attempt = (overrides: Partial<OnlinePaymentDto> = {}): OnlinePaymentDto => ({
  id: 'op1',
  enrollmentId: 'e1',
  studentId: 'st1',
  studentFirstName: 'Ayşe',
  studentLastName: 'Yılmaz',
  groupId: 'g1',
  groupName: 'U12 Mavi',
  period: '2026-11',
  amount: tl('1250.00'),
  provider: 'Fake',
  status: 'Pending',
  checkoutUrl: '/sandbox-odeme?token=fake-1',
  startedAt: '2026-11-07T10:00:00Z',
  checkoutExpiresAt: '2026-11-07T10:30:00Z',
  completedAt: null,
  failureCode: null,
  paymentId: null,
  overpaid: false,
  ...overrides,
});

describe('online payment helpers', () => {
  it('makes the sandbox link absolute so it can be shared', () => {
    expect(shareableCheckoutUrl('/sandbox-odeme?token=fake-1', 'https://okul.example.test')).toBe(
      'https://okul.example.test/sandbox-odeme?token=fake-1',
    );
    expect(shareableCheckoutUrl('https://sandbox-cpp.iyzipay.com?token=t', 'https://x.test')).toBe(
      'https://sandbox-cpp.iyzipay.com/?token=t',
    );
  });

  it('reads only known result statuses', () => {
    expect(resultStatus('Succeeded')).toBe('Succeeded');
    expect(resultStatus('Created')).toBe('Pending');
    expect(resultStatus('<script>')).toBe('Unknown');
    expect(resultStatus(null)).toBe('Unknown');
  });

  it('creates a fresh idempotency key per action', () => {
    const key = newIdempotencyKey();
    expect(key).toMatch(/^[A-Za-z0-9._:-]{8,100}$/);
    expect(newIdempotencyKey()).not.toBe(key);
  });

  it('posts the token to the callback like a hosted checkout', () => {
    const submit = vi
      .spyOn(HTMLFormElement.prototype, 'submit')
      .mockImplementation(() => undefined);
    postToCallback(document, '/api/v1/payment-callbacks', 'fake-1');
    const form = document.body.querySelector(
      'form[action="/api/v1/payment-callbacks"]',
    ) as HTMLFormElement;
    expect(form.method).toBe('post');
    expect((form.querySelector('input[name="token"]') as HTMLInputElement).value).toBe('fake-1');
    expect(submit).toHaveBeenCalled();
    form.remove();
    submit.mockRestore();
  });
});

describe('OnlinePaymentsPage', () => {
  it('lists the attempts of a due with status and the link to share', async () => {
    TestBed.configureTestingModule({
      imports: [OnlinePaymentsPage],
      providers: providers({ enrollmentId: 'e1', period: '2026-11' }, [
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
      ]),
    });
    const fixture = TestBed.createComponent(OnlinePaymentsPage);
    const backend = TestBed.inject(HttpTestingController);
    await settle(fixture);
    const request = backend.expectOne((r) => r.url === '/api/v1/online-payments');
    expect(request.request.params.get('enrollmentId')).toBe('e1');
    expect(request.request.params.get('period')).toBe('2026-11');
    request.flush({
      items: [
        attempt(),
        attempt({
          id: 'op0',
          status: 'Failed',
          checkoutUrl: null,
          failureCode: 'FakeDeclined',
        }),
        attempt({
          id: 'op2',
          status: 'Succeeded',
          checkoutUrl: null,
          paymentId: 'p2',
          overpaid: true,
        }),
      ],
      page: 1,
      pageSize: 25,
      totalCount: 3,
      totalPages: 1,
    });
    await settle(fixture);

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain(ui.onlinePayments.filteredByDue);
    const pending = root.querySelector('[data-testid="online-op1"]') as HTMLElement;
    expect(pending.textContent).toContain(ui.onlinePayments.statuses['Pending']);
    expect(pending.textContent).toContain('₺1.250,00');
    expect(pending.textContent).toContain(ui.onlinePayments.copyLink);
    const failed = root.querySelector('[data-testid="online-op0"]') as HTMLElement;
    expect(failed.textContent).toContain(ui.onlinePayments.statuses['Failed']);
    expect(failed.textContent).toContain('FakeDeclined');
    expect(failed.textContent).not.toContain(ui.onlinePayments.copyLink);
    const overpaid = root.querySelector('[data-testid="online-op2"]') as HTMLElement;
    expect(overpaid.querySelector('[data-testid="overpaid"]')?.textContent).toContain(
      ui.onlinePayments.overpaid,
    );
    expect(pending.querySelector('[data-testid="overpaid"]')).toBeNull();
    const options = [
      ...(root.querySelector('#online-filter-status') as HTMLSelectElement).options,
    ].map((o) => o.value);
    expect(options).toEqual(['', 'Created', 'Pending', 'Succeeded', 'Failed', 'Expired']);
    backend.verify();
  });
});

describe('DuesPage online payment', () => {
  const due: OutstandingDueDto = {
    enrollmentId: 'e1',
    studentId: 'st1',
    studentFirstName: 'Ayşe',
    studentLastName: 'Yılmaz',
    groupId: 'g1',
    groupName: 'U12 Mavi',
    period: '2026-11',
    fee: tl('1250.00'),
    paid: tl('0.00'),
    remaining: tl('1250.00'),
    listFee: tl('1250.00'),
    discount: tl('0.00'),
    adjustment: 'None',
    discountPercent: 0,
  };

  async function duesPage(toast: {
    success: ReturnType<typeof vi.fn>;
    error: ReturnType<typeof vi.fn>;
  }) {
    TestBed.configureTestingModule({
      imports: [DuesPage],
      providers: providers({}, [
        { provide: Dialog, useValue: { open: vi.fn() } },
        { provide: ToastService, useValue: toast },
      ]),
    });
    const fixture = TestBed.createComponent(DuesPage);
    const backend = TestBed.inject(HttpTestingController);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/groups')
      .flush({ items: [], page: 1, pageSize: 100, totalCount: 0, totalPages: 0 });
    for (const r of backend.match((r) => r.url === '/api/v1/dues/outstanding')) {
      r.flush({ items: [due], page: 1, pageSize: 25, totalCount: 1, totalPages: 1 });
    }
    await settle(fixture);
    const button = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find(
      (b) => b.textContent?.trim() === ui.billing.payOnline,
    ) as HTMLButtonElement;
    return { fixture, backend, navigate, button };
  }

  it("starts an online payment with an Idempotency-Key and shows the due's attempts", async () => {
    const toast = { success: vi.fn(), error: vi.fn() };
    const { fixture, backend, navigate, button } = await duesPage(toast);

    button.click();
    await settle(fixture);
    const start = backend.expectOne(
      (r) => r.url === '/api/v1/online-payments' && r.method === 'POST',
    );
    expect(start.request.headers.get('Idempotency-Key')).toMatch(/^[A-Za-z0-9._:-]{8,100}$/);
    expect(start.request.body).toEqual({ enrollmentId: 'e1', period: '2026-11' });
    start.flush(attempt());
    await settle(fixture);

    expect(toast.success).toHaveBeenCalledWith(ui.billing.onlineStarted);
    expect(navigate).toHaveBeenCalledWith(['/online-payments'], {
      queryParams: { enrollmentId: 'e1', period: '2026-11' },
    });
    backend.verify();
  });

  it('shows the open attempt when one is already in progress, and stays on other errors', async () => {
    const toast = { success: vi.fn(), error: vi.fn() };
    const { fixture, backend, navigate, button } = await duesPage(toast);

    button.click();
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/online-payments')
      .flush({ code: 'OnlinePayment.CheckoutInProgress' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    expect(toast.error).toHaveBeenCalledWith(errorMessages['OnlinePayment.CheckoutInProgress']);
    expect(navigate).toHaveBeenCalledTimes(1);

    button.click();
    await settle(fixture);
    backend
      .expectOne((r) => r.url === '/api/v1/online-payments')
      .flush({ code: 'OnlinePayment.NothingDue' }, { status: 422, statusText: 'Unprocessable' });
    await settle(fixture);
    expect(toast.error).toHaveBeenCalledWith(errorMessages['OnlinePayment.NothingDue']);
    expect(navigate).toHaveBeenCalledTimes(1);
    backend.verify();
  });
});

describe('public checkout pages', () => {
  it('completes the sandbox checkout and returns to the callback', async () => {
    const submit = vi
      .spyOn(HTMLFormElement.prototype, 'submit')
      .mockImplementation(() => undefined);
    TestBed.configureTestingModule({
      imports: [SandboxCheckoutPage],
      providers: providers({ token: 'fake-1' }),
    });
    const fixture = TestBed.createComponent(SandboxCheckoutPage);
    const backend = TestBed.inject(HttpTestingController);
    await settle(fixture);
    const pay = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find(
      (b) => b.textContent?.trim() === ui.onlinePayments.sandbox.pay,
    ) as HTMLButtonElement;

    pay.click();
    await settle(fixture);
    const complete = backend.expectOne('/api/v1/sandbox-checkout/complete');
    expect(complete.request.body).toEqual({ token: 'fake-1', succeed: true });
    complete.flush({ callbackUrl: '/api/v1/payment-callbacks' });
    await settle(fixture);

    expect(submit).toHaveBeenCalled();
    document.body
      .querySelectorAll('form[action="/api/v1/payment-callbacks"]')
      .forEach((f) => f.remove());
    submit.mockRestore();
    backend.verify();
  });

  it('shows the result for the status in the redirect', async () => {
    TestBed.configureTestingModule({
      imports: [PaymentResultPage],
      providers: providers({ durum: 'Succeeded' }),
    });
    const fixture = TestBed.createComponent(PaymentResultPage);
    await settle(fixture);

    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-testid="result-message"]')
        ?.textContent,
    ).toContain(ui.onlinePayments.result['Succeeded']);
  });

  it('are reached without signing in (the shell route falls through)', async () => {
    const { routes } = await import('../../app.routes');
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: { user: signal(null) } },
      ],
    });
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/odeme-sonucu?durum=Failed');

    expect(router.url).toBe('/odeme-sonucu?durum=Failed');
    expect(router.routerState.snapshot.root.firstChild?.routeConfig?.path).toBe('odeme-sonucu');
  });
});
