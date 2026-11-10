import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BillingRulesDto } from '../../api/models';
import { errorMessages, ui } from '../../core/i18n/tr';
import { SchoolClock } from '../../core/time/school-clock';
import { ToastService } from '../../shared/ui/toast.service';
import { BillingRulesPage, discounted } from './billing-rules.page';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

/** A month relative to the school's current one, as YYYY-MM. */
function monthFrom(today: string, offset: number): string {
  const [year, month] = today.slice(0, 7).split('-').map(Number);
  const date = new Date(Date.UTC(year, month - 1 + offset, 1));
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}`;
}

describe('discounted', () => {
  it('takes whole percents off in kuruş and rounds half away from zero like the API', () => {
    expect(discounted('800.00', 10)).toBe('720.00');
    expect(discounted('600.00', 15)).toBe('510.00');
    expect(discounted('100.05', 50)).toBe('50.03');
    expect(discounted('333.33', 10)).toBe('300.00');
    expect(discounted('999.99', 0)).toBe('999.99');
    expect(discounted('999.99', 100)).toBe('0.00');
  });
});

describe('BillingRulesPage', () => {
  let fixture: ComponentFixture<BillingRulesPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  let thisMonth: string;
  const toasts = { success: vi.fn(), error: vi.fn() };

  const version = (
    effectiveFrom: string,
    extra: Partial<BillingRulesDto> = {},
  ): BillingRulesDto => ({
    id: `id-${effectiveFrom}`,
    effectiveFrom,
    secondChildPercent: 10,
    thirdAndLaterChildPercent: 15,
    singleChargeOnGroupChange: true,
    ...extra,
  });

  beforeEach(async () => {
    toasts.success.mockClear();
    toasts.error.mockClear();
    TestBed.configureTestingModule({
      imports: [BillingRulesPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: toasts },
      ],
    });
    fixture = TestBed.createComponent(BillingRulesPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    thisMonth = TestBed.inject(SchoolClock).today().slice(0, 7);
    fixture.detectChanges();
  });

  afterEach(() => backend.verify());

  const list = () =>
    backend.expectOne((r) => r.url === '/api/v1/settings/billing-rules' && r.method === 'GET');
  const submit = async () => {
    (root.querySelector('form') as HTMLFormElement).requestSubmit();
    await settle(fixture);
  };
  async function type(id: string, value: string): Promise<void> {
    const el = root.querySelector(`#${id}`) as HTMLInputElement;
    el.value = value;
    el.dispatchEvent(new Event('input'));
    await settle(fixture);
  }

  it('explains the rules with an example from the suggested defaults', async () => {
    list().flush([]);
    await settle(fixture);

    expect((root.querySelector('#rules-second') as HTMLInputElement).value).toBe('10');
    expect((root.querySelector('#rules-third') as HTMLInputElement).value).toBe('15');
    expect((root.querySelector('#rules-single-charge') as HTMLInputElement).checked).toBe(true);
    expect((root.querySelector('#rules-from') as HTMLInputElement).value).toBe(thisMonth);
    const example = root.querySelector('[data-testid="rules-example"]')!;
    expect(example.getAttribute('aria-live')).toBe('polite');
    expect(example.textContent).toContain('2. kardeş %10, 3. ve sonraki %15');
    expect(example.textContent).toContain('₺1.000,00, ₺720,00 ve ₺510,00 öder');
    expect(example.textContent).toContain('en yüksek aidatlı kardeş tam öder');
    expect(root.textContent).toContain(ui.billingRules.none);

    await type('rules-second', '20');
    expect(root.querySelector('[data-testid="rules-example"]')?.textContent).toContain('₺640,00');
  });

  it('lists versions newest first with the one in force marked current and later ones upcoming', async () => {
    list().flush([
      version(monthFrom(thisMonth, 2), { secondChildPercent: 5, singleChargeOnGroupChange: false }),
      version(monthFrom(thisMonth, -3)),
      version(monthFrom(thisMonth, -6)),
    ]);
    await settle(fixture);

    const rows = [...root.querySelectorAll('tbody tr')];
    expect(rows).toHaveLength(3);
    expect(rows[0].textContent).toContain(ui.billingRules.upcoming);
    expect(rows[0].textContent).toContain('%5');
    expect(rows[0].textContent).toContain(ui.billingRules.singleChargeOff);
    expect(rows[1].textContent).toContain(ui.billingRules.current);
    expect(rows[1].textContent).toContain(ui.billingRules.singleChargeOn);
    expect(rows[2].textContent).not.toContain(ui.billingRules.current);
    expect(root.querySelector('caption')?.textContent).toContain(ui.billingRules.historyCaption);
  });

  it('defines a version and shows the returned list', async () => {
    list().flush([]);
    await settle(fixture);
    await type('rules-second', '12');
    (root.querySelector('#rules-single-charge') as HTMLInputElement).click();
    await settle(fixture);
    await submit();

    const post = backend.expectOne((r) => r.method === 'POST');
    expect(post.request.url).toBe('/api/v1/settings/billing-rules');
    expect(post.request.body).toEqual({
      effectiveFrom: thisMonth,
      secondChildPercent: 12,
      thirdAndLaterChildPercent: 15,
      singleChargeOnGroupChange: false,
    });
    post.flush([version(thisMonth, { secondChildPercent: 12, singleChargeOnGroupChange: false })], {
      status: 201,
      statusText: 'Created',
    });
    await settle(fixture);

    expect(toasts.success).toHaveBeenCalledWith(ui.billingRules.saved);
    expect(root.querySelectorAll('tbody tr')).toHaveLength(1);
    expect(root.querySelector('tbody tr')?.textContent).toContain('%12');
  });

  it('refuses invalid percents and a past month on the client and focuses the first one', async () => {
    list().flush([]);
    await settle(fixture);
    await type('rules-from', monthFrom(thisMonth, -1));
    await type('rules-second', '101');
    await type('rules-third', '7.5');
    await submit();

    backend.expectNone((r) => r.method === 'POST');
    expect(root.querySelector('#rules-from-error')?.textContent).toContain(
      errorMessages['BillingRules.PeriodInPast'],
    );
    expect(root.querySelector('#rules-second-error')?.textContent).toContain(
      errorMessages['BillingRules.InvalidPercent'],
    );
    expect(root.querySelector('#rules-third-error')?.textContent).toContain(
      errorMessages['BillingRules.InvalidPercent'],
    );
    expect(root.querySelector('#rules-second')?.getAttribute('aria-invalid')).toBe('true');
    expect(root.querySelector('#rules-second')?.getAttribute('aria-describedby')).toContain(
      'rules-second-error',
    );
    expect(document.activeElement?.id).toBe('rules-from');
  });

  it('shows an already defined month on the month field and other problems in an alert', async () => {
    list().flush([]);
    await settle(fixture);
    await submit();
    backend
      .expectOne((r) => r.method === 'POST')
      .flush({ code: 'BillingRules.AlreadyDefined' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);
    expect(root.querySelector('#rules-from-error')?.textContent).toContain(
      errorMessages['BillingRules.AlreadyDefined'],
    );

    await submit();
    backend
      .expectOne((r) => r.method === 'POST')
      .flush(
        {
          code: 'Validation.Failed',
          errors: { secondChildPercent: ['BillingRules.InvalidPercent'] },
        },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle(fixture);
    expect(root.querySelector('#rules-second-error')?.textContent).toContain(
      errorMessages['BillingRules.InvalidPercent'],
    );

    await submit();
    backend
      .expectOne((r) => r.method === 'POST')
      .flush({ code: 'Unexpected.Error' }, { status: 500, statusText: 'Server Error' });
    await settle(fixture);
    expect(root.querySelector('[role="alert"]')?.textContent).toContain(
      errorMessages['Unexpected.Error'].slice(0, 20),
    );
  });

  it('offers a retry when the list cannot be loaded', async () => {
    list().flush({ code: 'Unexpected.Error' }, { status: 500, statusText: 'Server Error' });
    await settle(fixture);
    const retry = [...root.querySelectorAll('button')].find((b) =>
      b.textContent?.includes(ui.common.retry),
    ) as HTMLButtonElement;
    retry.click();
    list().flush([version(thisMonth)]);
    await settle(fixture);
    expect(root.querySelectorAll('tbody tr')).toHaveLength(1);
  });
});
