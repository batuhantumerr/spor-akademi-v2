import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';

import { ApiProblem } from '../../core/api/problem';
import { MfaEnrollmentStart } from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';
import { MfaSetupComponent, groupedSecret, qrPath } from './mfa-setup.component';

/** A synthetic setup key, put together at run time so no key-shaped literal sits in the source. */
const setupKey = ['GEZD', 'GNBV', 'GY3T', 'QOJQ'].join('');

const started: MfaEnrollmentStart = {
  otpAuthUri: `otpauth://totp/Spor%20Akademi:admin%40example.test?secret=${setupKey}&issuer=Spor%20Akademi`,
  secret: setupKey,
};

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

function type(root: HTMLElement, id: string, value: string): void {
  const input = root.querySelector<HTMLInputElement>(`#${id}`)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('qrPath', () => {
  it('draws one unit square per dark module inside a four-module quiet zone', () => {
    const { path, size } = qrPath(started.otpAuthUri);

    // 21 modules is the smallest QR code; an otpauth URI needs a larger one. The quiet zone is 4 on each side.
    expect(size).toBeGreaterThanOrEqual(21 + 8);
    // The top-left finder pattern starts with a dark module at (0, 0), drawn after the quiet zone.
    expect(path.startsWith('M4 4h1v1h-1z')).toBe(true);
    expect(path).not.toContain('NaN');
  });

  it('is the same picture for the same text', () => {
    expect(qrPath(started.otpAuthUri)).toEqual(qrPath(started.otpAuthUri));
  });
});

describe('groupedSecret', () => {
  it('writes the key in groups of four', () => {
    expect(groupedSecret(setupKey)).toBe('GEZD GNBV GY3T QOJQ');
    expect(groupedSecret('ABCDEF')).toBe('ABCD EF');
  });
});

describe('MfaSetupComponent', () => {
  let fixture: ComponentFixture<MfaSetupComponent>;
  let root: HTMLElement;
  let failures: ApiProblem[];
  let finished: number;

  function open(confirm: (code: string) => Observable<string[]>): void {
    fixture = TestBed.createComponent(MfaSetupComponent);
    root = fixture.nativeElement as HTMLElement;
    failures = [];
    finished = 0;
    fixture.componentRef.setInput('begin', () => of(started));
    fixture.componentRef.setInput('confirm', confirm);
    fixture.componentInstance.failed.subscribe((p) => failures.push(p));
    fixture.componentInstance.finished.subscribe(() => finished++);
    fixture.detectChanges();
  }

  const submit = () =>
    root.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));

  it('shows the QR code and the key as text, and names the picture for screen readers', async () => {
    open(() => of([]));
    await settle(fixture);

    const picture = root.querySelector('svg[role="img"]')!;
    expect(picture.getAttribute('aria-label')).toBe(ui.mfa.setup.qrLabel);
    expect(picture.querySelector('path')!.getAttribute('d')).toBe(qrPath(started.otpAuthUri).path);
    const key = root.querySelector<HTMLInputElement>('#mfa-secret')!;
    expect(key.value).toBe('GEZD GNBV GY3T QOJQ');
    expect(root.querySelector('label[for="mfa-secret"]')!.textContent).toContain(
      ui.mfa.setup.secretLabel,
    );
  });

  it('asks for the code, then shows the recovery codes until they are saved', async () => {
    let sent: string | undefined;
    open((code) => {
      sent = code;
      return of(['aaaa-bbbb-cccc-dddd', 'eeee-ffff-gggg-hhhh']);
    });
    await settle(fixture);

    submit();
    fixture.detectChanges();
    expect(root.querySelector('#mfa-setup-code-error')!.textContent).toContain(
      errorMessages['Field.Required'],
    );
    expect(sent).toBeUndefined();

    type(root, 'mfa-setup-code', '123 456');
    submit();
    await settle(fixture);

    expect(sent).toBe('123 456');
    const codes = [...root.querySelectorAll('ul li')].map((li) => li.textContent?.trim());
    expect(codes).toEqual(['aaaa-bbbb-cccc-dddd', 'eeee-ffff-gggg-hhhh']);
    // The setup form is gone: the key and the QR code are not shown again.
    expect(root.querySelector('#mfa-secret')).toBeNull();
    // Focus moves to the heading so a screen reader announces the codes.
    expect(document.activeElement).toBe(root.querySelector('#mfa-codes-title'));

    const next = [...root.querySelectorAll('button')].find(
      (b) => b.textContent?.trim() === ui.mfa.setup.continue,
    )!;
    expect(next.disabled).toBe(true);
    root.querySelector<HTMLInputElement>('#mfa-codes-saved')!.click();
    fixture.detectChanges();
    expect(next.disabled).toBe(false);
    next.click();
    expect(finished).toBe(1);
  });

  it('shows the translated error of a wrong code, reports it and lets the user try again', async () => {
    open(() =>
      throwError(() => new HttpErrorResponse({ status: 422, error: { code: 'Mfa.CodeInvalid' } })),
    );
    await settle(fixture);

    type(root, 'mfa-setup-code', '000000');
    submit();
    await settle(fixture);

    expect(root.querySelector('[role="alert"]')!.textContent).toContain(
      errorMessages['Mfa.CodeInvalid'],
    );
    expect(failures.map((f) => f.code)).toEqual(['Mfa.CodeInvalid']);
    expect(root.querySelector<HTMLInputElement>('#mfa-setup-code')!.value).toBe('');
    expect(finished).toBe(0);
  });
});
