import { Dialog } from '@angular/cdk/dialog';
import { TestBed } from '@angular/core/testing';

import { ui } from '../../core/i18n/tr';
import {
  TemporaryPasswordData,
  TemporaryPasswordDialog,
  TemporaryPasswordService,
} from './temporary-password.dialog';

const settle = () => new Promise((resolve) => setTimeout(resolve));

describe('TemporaryPasswordService', () => {
  const password = 'Gecici-8kQ2-x9';

  afterEach(() => {
    TestBed.inject(Dialog).closeAll();
  });

  function doneButton(): HTMLButtonElement {
    return [...document.querySelectorAll('button')].find(
      (b) => b.textContent?.trim() === ui.temporaryPassword.done,
    ) as HTMLButtonElement;
  }

  it('shows the password once with a warning and wipes it on close', async () => {
    const service = TestBed.inject(TemporaryPasswordService);
    const data: TemporaryPasswordData = { name: 'Ayşe Yılmaz', password };
    let closed = false;
    void service.show(data).then(() => (closed = true));
    await settle();

    const input = document.querySelector('#temporary-password') as HTMLInputElement;
    expect(input.value).toBe(password);
    expect(input.readOnly).toBe(true);
    expect(document.body.textContent).toContain(ui.temporaryPassword.warning);
    expect(document.body.textContent).toContain('Ayşe Yılmaz için geçici şifre');
    // The caller's object no longer holds the password.
    expect(data.password).toBe('');

    const container = document.querySelector('[role="alertdialog"]') as HTMLElement;
    expect(container.getAttribute('aria-labelledby')).toBe('temporary-password-title');
    expect(container.getAttribute('aria-describedby')).toBe('temporary-password-warning');

    const dialog = TestBed.inject(Dialog).openDialogs[0]
      .componentInstance as TemporaryPasswordDialog;
    doneButton().click();
    await settle();

    expect(closed).toBe(true);
    expect(dialog.password()).toBe('');
    // The detached input element must not keep the value either.
    expect(input.value).toBe('');
    expect(document.querySelector('#temporary-password')).toBeNull();
  });

  it('does not close on Escape, so the password cannot be lost by accident', async () => {
    void TestBed.inject(TemporaryPasswordService).show({ name: 'X', password });
    await settle();

    document
      .querySelector('#temporary-password')!
      .dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await settle();

    expect(TestBed.inject(Dialog).openDialogs.length).toBe(1);
  });

  it('stays open when the app navigates', async () => {
    void TestBed.inject(TemporaryPasswordService).show({ name: 'X', password });
    await settle();
    expect(TestBed.inject(Dialog).openDialogs[0].config.closeOnNavigation).toBe(false);
  });

  it('tells the user to copy by hand when the clipboard is unavailable', async () => {
    const writeText = vi.fn().mockRejectedValue(new Error('denied'));
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    void TestBed.inject(TemporaryPasswordService).show({ name: 'X', password });
    await settle();

    const copy = [...document.querySelectorAll('button')].find((b) =>
      b.textContent?.includes(ui.temporaryPassword.copy),
    ) as HTMLButtonElement;
    copy.click();
    await settle();
    TestBed.inject(Dialog).openDialogs[0].componentRef?.changeDetectorRef.detectChanges();

    expect(document.querySelector('[role="status"]')?.textContent).toContain(
      ui.temporaryPassword.copyFailed,
    );
    expect(document.querySelector('[role="status"]')?.textContent).not.toContain(
      ui.temporaryPassword.copied,
    );
  });

  it('copies to the clipboard and announces the result', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    void TestBed.inject(TemporaryPasswordService).show({ name: 'X', password });
    await settle();

    const copy = [...document.querySelectorAll('button')].find((b) =>
      b.textContent?.includes(ui.temporaryPassword.copy),
    ) as HTMLButtonElement;
    copy.click();
    await settle();
    TestBed.inject(Dialog).openDialogs[0].componentRef?.changeDetectorRef.detectChanges();

    expect(writeText).toHaveBeenCalledWith(password);
    expect(document.querySelector('[role="status"]')?.textContent).toContain(
      ui.temporaryPassword.copied,
    );
  });
});
