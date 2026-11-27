import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { AuthService, CurrentUser, SIGNED_IN_FLAG } from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';
import { LoginPage } from './login.page';

const platformAdmin: CurrentUser = {
  id: 'u1',
  email: 'platform@example.test',
  firstName: 'Platform',
  lastName: 'Admin',
  role: 'PlatformAdmin',
  tenantId: null,
  mustChangePassword: false,
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

/** A synthetic setup key, put together at run time so no key-shaped literal sits in the source. */
const setupKey = ['GEZD', 'GNBV', 'GY3T', 'QOJQ'].join('');

describe('LoginPage two-step verification (P5-01)', () => {
  let fixture: ComponentFixture<LoginPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;
  let navigate: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    // jsdom has no matchMedia; ThemeService reads the system preference from it.
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: () => ({
        matches: false,
        addEventListener: () => undefined,
        removeEventListener: () => undefined,
      }),
    });
    TestBed.configureTestingModule({
      imports: [LoginPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    fixture = TestBed.createComponent(LoginPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => backend.verify());

  const submit = () =>
    root.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));

  async function signInWith(purpose: 'Verify' | 'Enroll'): Promise<void> {
    type(root, 'email', 'platform@example.test');
    type(root, 'password', 'Sentetik-Sifre-2027');
    submit();
    fixture.detectChanges();
    backend
      .expectOne('/api/v1/auth/login')
      .flush(
        { purpose, mfaToken: 'challenge-1', expiresIn: 300 },
        { status: 202, statusText: 'Accepted' },
      );
    await settle(fixture);
  }

  it('asks for the code after the password and signs in with it', async () => {
    await signInWith('Verify');

    expect(root.querySelector('h1')!.textContent).toContain(ui.mfa.login.codeTitle);
    expect(root.querySelector('#password')).toBeNull();
    expect(navigate).not.toHaveBeenCalled();
    const code = root.querySelector<HTMLInputElement>('#mfa-code')!;
    expect(code.getAttribute('autocomplete')).toBe('one-time-code');
    expect(code.getAttribute('inputmode')).toBe('numeric');

    type(root, 'mfa-code', '123456');
    submit();
    fixture.detectChanges();
    const verify = backend.expectOne('/api/v1/auth/mfa/verify');
    expect(verify.request.body).toEqual({ mfaToken: 'challenge-1', code: '123456' });
    verify.flush({ accessToken: 'token-1', expiresIn: 900 });
    backend.expectOne('/api/v1/me').flush(platformAdmin);
    await settle(fixture);

    expect(navigate).toHaveBeenCalledWith('/platform/schools');
  });

  it('switches to a recovery code and back', async () => {
    await signInWith('Verify');

    const toggle = () =>
      [...root.querySelectorAll('button')].find((b) =>
        [ui.mfa.login.useRecovery, ui.mfa.login.useApp].includes(b.textContent!.trim()),
      )!;
    toggle().click();
    fixture.detectChanges();

    expect(root.querySelector('label[for="mfa-code"]')!.textContent).toContain(
      ui.mfa.login.recoveryCode,
    );
    expect(root.querySelector('#mfa-code')!.getAttribute('inputmode')).toBe('text');
    toggle().click();
    fixture.detectChanges();
    expect(root.querySelector('label[for="mfa-code"]')!.textContent).toContain(ui.mfa.login.code);
  });

  it('keeps the code step open after a wrong code and says so', async () => {
    await signInWith('Verify');

    type(root, 'mfa-code', '000000');
    submit();
    fixture.detectChanges();
    backend
      .expectOne('/api/v1/auth/mfa/verify')
      .flush({ code: 'Mfa.CodeInvalid' }, { status: 422, statusText: 'Unprocessable' });
    await settle(fixture);

    expect(root.querySelector('[role="alert"]')!.textContent).toContain(
      errorMessages['Mfa.CodeInvalid'],
    );
    expect(root.querySelector('#mfa-code')).not.toBeNull();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('starts over at the password when the challenge has expired', async () => {
    await signInWith('Verify');

    type(root, 'mfa-code', '123456');
    submit();
    fixture.detectChanges();
    backend
      .expectOne('/api/v1/auth/mfa/verify')
      .flush({ code: 'Mfa.ChallengeInvalid' }, { status: 401, statusText: 'Unauthorized' });
    await settle(fixture);

    expect(root.querySelector('#mfa-code')).toBeNull();
    expect(root.querySelector('#password')).not.toBeNull();
    expect(root.querySelector('[role="alert"]')!.textContent).toContain(
      errorMessages['Mfa.ChallengeInvalid'],
    );
  });

  it('sets two-step verification up first when the role requires it, then signs in behind the recovery codes', async () => {
    await signInWith('Enroll');

    expect(root.querySelector('h1')!.textContent).toContain(ui.mfa.login.enrollTitle);
    const start = backend.expectOne('/api/v1/auth/mfa/enrollment/start');
    expect(start.request.body).toEqual({ mfaToken: 'challenge-1' });
    start.flush({
      otpAuthUri: `otpauth://totp/x?secret=${setupKey}`,
      secret: setupKey,
    });
    await settle(fixture);
    expect(root.querySelector('svg[role="img"]')).not.toBeNull();

    type(root, 'mfa-setup-code', '123456');
    submit();
    fixture.detectChanges();
    backend
      .expectOne('/api/v1/auth/mfa/enrollment/confirm')
      .flush({ accessToken: 'token-1', expiresIn: 900, recoveryCodes: ['aaaa-bbbb-cccc-dddd'] });
    backend.expectOne('/api/v1/me').flush(platformAdmin);
    await settle(fixture);

    // The session exists, but the page waits until the codes are saved.
    expect(navigate).not.toHaveBeenCalled();
    expect(
      root.querySelector(`ul[aria-label="${ui.mfa.setup.codesList}"] li`)!.textContent,
    ).toContain('aaaa-bbbb-cccc-dddd');
    root.querySelector<HTMLInputElement>('#mfa-codes-saved')!.click();
    fixture.detectChanges();
    [...root.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === ui.mfa.setup.continue)!
      .click();

    expect(navigate).toHaveBeenCalledWith('/platform/schools');
  });
});

describe('LoginPage expired session notice (V1-03)', () => {
  beforeEach(() => {
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: () => ({
        matches: false,
        addEventListener: () => undefined,
        removeEventListener: () => undefined,
      }),
    });
    TestBed.configureTestingModule({
      imports: [LoginPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  it('says the session expired once the service found it so on reload, and only then', async () => {
    const auth = TestBed.inject(AuthService);
    const fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[role="alert"]')).toBeNull();

    // The startup restore settles after the page is up, as it can.
    localStorage.setItem(SIGNED_IN_FLAG, '1');
    const boot = auth.restoreAtBoot(5000);
    TestBed.inject(HttpTestingController)
      .expectOne('/api/v1/auth/refresh')
      .flush({ code: 'Auth.SessionExpired' }, { status: 401, statusText: 'Unauthorized' });
    await boot;
    await settle(fixture);

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(
      errorMessages['Auth.SessionExpired'],
    );
    expect(auth.expiredNotice()).toBe(false); // shown once
  });
});
