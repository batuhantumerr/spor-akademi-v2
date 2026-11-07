import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ui } from '../../core/i18n/tr';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { ToastService } from '../../shared/ui/toast.service';
import { DataRetentionPage } from './data-retention.page';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

const policy = {
  personalDataMonths: 24,
  paymentYears: 10,
  minPersonalDataMonths: 6,
  maxPersonalDataMonths: 120,
  version: 7,
};

const run = {
  id: '0190a000-0000-7000-8000-000000000001',
  startedAt: '2027-01-14T02:00:00+00:00',
  durationMs: 42,
  cutoff: '2025-01-14',
  personalDataMonths: 24,
  anonymizedCount: 3,
  skippedCount: 0,
  status: 'Succeeded',
};

describe('DataRetentionPage', () => {
  let fixture: ComponentFixture<DataRetentionPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  const toasts = { success: vi.fn(), error: vi.fn() };
  const confirm =
    vi.fn<(options: { message: string; destructive?: boolean }) => Promise<boolean>>();

  beforeEach(async () => {
    toasts.success.mockClear();
    toasts.error.mockClear();
    confirm.mockReset().mockResolvedValue(true);
    TestBed.configureTestingModule({
      imports: [DataRetentionPage],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: toasts },
        { provide: ConfirmService, useValue: { confirm } },
      ],
    });
    fixture = TestBed.createComponent(DataRetentionPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    backend.expectOne('/api/v1/settings/data-retention').flush(policy);
    backend.expectOne('/api/v1/settings/data-retention/runs').flush([run]);
    await settle(fixture);
  });

  afterEach(() => backend.verify());

  const input = () => root.querySelector<HTMLInputElement>('#retention-months')!;
  const submit = () => root.querySelector<HTMLButtonElement>('button[type="submit"]')!;

  function type(value: string): void {
    input().value = value;
    input().dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('shows the policy, the fixed payment period and the latest runs', () => {
    expect(input().value).toBe('24');
    expect(root.textContent).toContain('6–120 ay');
    expect(root.textContent).toContain('10 yıl');
    const cells = Array.from(root.querySelectorAll('tbody td')).map((c) => c.textContent?.trim());
    expect(cells).toContain('14.01.2025');
    expect(cells).toContain('3');
    expect(cells).toContain(ui.dataRetention.statuses['Succeeded']);
    expect(root.querySelector('#retention-months-hint')?.getAttribute('aria-live')).toBe('polite');
  });

  it('marks a failed run as an error', async () => {
    backend.match(() => true);
    fixture.componentInstance.load();
    backend.expectOne('/api/v1/settings/data-retention').flush(policy);
    backend.expectOne('/api/v1/settings/data-retention/runs').flush([{ ...run, status: 'Failed' }]);
    await settle(fixture);

    const badge = root.querySelector('tbody .badge')!;
    expect(badge.textContent?.trim()).toBe(ui.dataRetention.statuses['Failed']);
    expect(badge.classList).toContain('badge-danger');
  });

  it('saves a period within the bounds with the version it read', async () => {
    type('36');
    submit().click();
    const request = backend.expectOne('/api/v1/settings/data-retention');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ personalDataMonths: 36, version: 7 });
    request.flush({ ...policy, personalDataMonths: 36, version: 8 });
    await settle(fixture);

    expect(toasts.success).toHaveBeenCalledWith(ui.dataRetention.saved);
    expect(input().value).toBe('36');
    expect(confirm).not.toHaveBeenCalled();
  });

  it('asks before a shorter period and tells how many students it anonymizes tonight', async () => {
    type('12');
    submit().click();
    backend
      .expectOne('/api/v1/settings/data-retention/impact?personalDataMonths=12')
      .flush({ personalDataMonths: 12, cutoff: '2026-01-14', dueCount: 4 });
    await settle(fixture);

    expect(confirm).toHaveBeenCalledTimes(1);
    const options = confirm.mock.calls[0][0];
    expect(options.message).toContain('4 öğrencinin');
    expect(options.message).toContain('14.01.2026');
    expect(options.destructive).toBe(true);
    const request = backend.expectOne((r) => r.method === 'PUT');
    expect(request.request.body).toEqual({ personalDataMonths: 12, version: 7 });
    request.flush({ ...policy, personalDataMonths: 12, version: 8 });
    await settle(fixture);
    expect(toasts.success).toHaveBeenCalledWith(ui.dataRetention.saved);
  });

  it('keeps the period when the admin declines the shorter one', async () => {
    confirm.mockResolvedValue(false);
    type('6');
    submit().click();
    backend
      .expectOne('/api/v1/settings/data-retention/impact?personalDataMonths=6')
      .flush({ personalDataMonths: 6, cutoff: '2026-07-14', dueCount: 9 });
    await settle(fixture);

    backend.expectNone((r) => r.method === 'PUT');
    expect(toasts.success).not.toHaveBeenCalled();
    expect(submit().disabled).toBe(false);
  });

  it('refuses a period outside the bounds without calling the API', () => {
    type('5');
    submit().click();
    fixture.detectChanges();

    backend.expectNone('/api/v1/settings/data-retention');
    expect(input().getAttribute('aria-invalid')).toBe('true');
  });

  it('reloads the current value after a concurrent change', async () => {
    type('30');
    submit().click();
    backend
      .expectOne((r) => r.method === 'PUT')
      .flush({ code: 'Concurrency.Conflict' }, { status: 409, statusText: 'Conflict' });
    await settle(fixture);

    expect(toasts.error).toHaveBeenCalledWith(ui.dataRetention.conflict);
    backend
      .expectOne('/api/v1/settings/data-retention')
      .flush({ ...policy, personalDataMonths: 48 });
    backend.expectOne('/api/v1/settings/data-retention/runs').flush([]);
    await settle(fixture);
    expect(input().value).toBe('48');
    expect(root.textContent).toContain(ui.dataRetention.noRuns);
  });
});
