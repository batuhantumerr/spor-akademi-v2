import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { MfaStatus } from '../../core/auth/mfa.service';
import { interpolate, ui } from '../../core/i18n/tr';
import { ToastService } from '../../shared/ui/toast.service';
import { SecurityPage } from './security.page';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

/** A synthetic setup key, put together at run time so no key-shaped literal sits in the source. */
const setupKey = ['GEZD', 'GNBV', 'GY3T', 'QOJQ'].join('');

describe('SecurityPage (P5-01)', () => {
  let fixture: ComponentFixture<SecurityPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SecurityPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(SecurityPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => backend.verify());

  async function load(status: MfaStatus): Promise<void> {
    backend.expectOne('/api/v1/me/mfa').flush(status);
    await settle(fixture);
  }

  const button = (label: string) =>
    [...root.querySelectorAll('button')].find((b) => b.textContent?.trim() === label);

  it('offers to turn it on for a School Admin without it', async () => {
    await load({ available: true, enabled: false, required: false, recoveryCodesRemaining: 0 });

    expect(root.textContent).toContain(ui.mfa.settings.off);
    expect(root.textContent).toContain(ui.mfa.settings.offText);
    expect(button(ui.mfa.settings.enable)).toBeDefined();
    expect(button(ui.mfa.settings.disable)).toBeUndefined();
  });

  it('walks through the setup and shows the new state afterwards', async () => {
    await load({ available: true, enabled: false, required: false, recoveryCodesRemaining: 0 });

    button(ui.mfa.settings.enable)!.click();
    fixture.detectChanges();
    backend.expectOne('/api/v1/me/mfa/enrollment').flush({
      otpAuthUri: `otpauth://totp/x?secret=${setupKey}`,
      secret: setupKey,
    });
    await settle(fixture);
    expect(root.querySelector('svg[role="img"]')).not.toBeNull();

    const input = root.querySelector<HTMLInputElement>('#mfa-setup-code')!;
    input.value = '123456';
    input.dispatchEvent(new Event('input'));
    root.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
    const confirm = backend.expectOne('/api/v1/me/mfa/enrollment/confirm');
    expect(confirm.request.body).toEqual({ code: '123456' });
    confirm.flush({ codes: ['aaaa-bbbb-cccc-dddd'] });
    await settle(fixture);

    root.querySelector<HTMLInputElement>('#mfa-codes-saved')!.click();
    fixture.detectChanges();
    button(ui.mfa.setup.continue)!.click();
    await settle(fixture);
    await load({ available: true, enabled: true, required: false, recoveryCodesRemaining: 10 });

    expect(root.textContent).toContain(ui.mfa.settings.on);
    expect(root.textContent).toContain(interpolate(ui.mfa.settings.onText, { remaining: 10 }));
  });

  it('turns it off through a dialog that asks for a code, and reloads', async () => {
    const dialog = TestBed.inject(Dialog);
    vi.spyOn(dialog, 'open').mockReturnValue({ closed: of(true) } as never);
    const success = vi.spyOn(TestBed.inject(ToastService), 'success');
    await load({ available: true, enabled: true, required: false, recoveryCodesRemaining: 9 });

    button(ui.mfa.settings.disable)!.click();
    await settle(fixture);

    expect(dialog.open).toHaveBeenCalledTimes(1);
    expect(success).toHaveBeenCalledWith(ui.mfa.settings.disabled);
    backend.expectOne('/api/v1/me/mfa').flush({
      available: true,
      enabled: false,
      required: false,
      recoveryCodesRemaining: 0,
    });
  });

  it('has no way to turn it off where it is mandatory', async () => {
    await load({ available: true, enabled: true, required: true, recoveryCodesRemaining: 10 });

    expect(root.textContent).toContain(ui.mfa.settings.requiredText);
    expect(button(ui.mfa.settings.disable)).toBeUndefined();
    expect(button(ui.mfa.settings.enable)).toBeUndefined();
  });

  it('says it is not available to a role that cannot use it', async () => {
    await load({ available: false, enabled: false, required: false, recoveryCodesRemaining: 0 });

    expect(root.textContent).toContain(ui.mfa.settings.unavailable);
    expect(button(ui.mfa.settings.enable)).toBeUndefined();
  });
});
