import { Location } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { errorMessages, ui } from '../../core/i18n/tr';
import { ForgotPasswordPage } from './forgot-password.page';
import { ResetPasswordPage, tokenFromFragment } from './reset-password.page';

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve));
  fixture.detectChanges();
}

function type(root: HTMLElement, id: string, value: string): void {
  const input = root.querySelector<HTMLInputElement>(`#${id}`)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('password reset routes', () => {
  async function open(url: string): Promise<RouterTestingHarness> {
    const { routes } = await import('../../app.routes');
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    return harness;
  }

  it('opens "Şifremi unuttum" for a signed-out visitor', async () => {
    const harness = await open('/forgot-password');

    expect(TestBed.inject(Router).url).toBe('/forgot-password');
    expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(ForgotPasswordPage);
  });

  it('opens "Yeni şifre belirle" from an e-mail link and drops the token from the address', async () => {
    const harness = await open('/reset-password#token=abc');

    expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(ResetPasswordPage);
    expect(TestBed.inject(Location).path(true)).toBe('/reset-password');
  });
});

describe('ForgotPasswordPage', () => {
  let fixture: ComponentFixture<ForgotPasswordPage>;
  let backend: HttpTestingController;
  let root: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ForgotPasswordPage],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(ForgotPasswordPage);
    backend = TestBed.inject(HttpTestingController);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => backend.verify());

  const submit = () =>
    root.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));

  it('shows the same confirmation whatever the address, after the API accepts it', async () => {
    type(root, 'email', 'ayse@example.test');
    submit();
    fixture.detectChanges();

    const request = backend.expectOne('/api/v1/auth/password-reset/request');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email: 'ayse@example.test' });
    request.flush(null, { status: 202, statusText: 'Accepted' });
    await settle(fixture);

    expect(root.querySelector('[role="status"]')?.textContent).toContain(ui.forgotPassword.sent);
    expect(root.querySelector('form')).toBeNull();
    expect(root.querySelector('a[href="/login"]')).not.toBeNull();
  });

  it('validates the address before calling the API', () => {
    type(root, 'email', 'gecersiz');
    submit();
    fixture.detectChanges();

    backend.expectNone('/api/v1/auth/password-reset/request');
    expect(root.querySelector('#email-error')?.textContent).toContain(
      errorMessages['Email.Invalid'],
    );
  });

  it('explains a rate limit', async () => {
    type(root, 'email', 'ayse@example.test');
    submit();
    backend
      .expectOne('/api/v1/auth/password-reset/request')
      .flush({ code: 'Request.TooManyRequests' }, { status: 429, statusText: 'Too Many Requests' });
    await settle(fixture);

    expect(root.querySelector('[role="alert"]')?.textContent).toContain(
      errorMessages['Request.TooManyRequests'],
    );
    expect(root.querySelector('form')).not.toBeNull();
  });
});

describe('ResetPasswordPage', () => {
  let backend: HttpTestingController;

  function create(fragment: string | null): {
    fixture: ComponentFixture<ResetPasswordPage>;
    root: HTMLElement;
    replaceState: ReturnType<typeof vi.spyOn>;
  } {
    TestBed.configureTestingModule({
      imports: [ResetPasswordPage],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { fragment } } },
      ],
    });
    const replaceState = vi.spyOn(TestBed.inject(Location), 'replaceState');
    const fixture = TestBed.createComponent(ResetPasswordPage);
    backend = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement, replaceState };
  }

  afterEach(() => backend.verify());

  const submit = (root: HTMLElement) =>
    root.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));

  it('reads the token from the fragment and removes it from the address bar', () => {
    const { replaceState } = create('token=abc_DEF-123');

    expect(replaceState).toHaveBeenCalledWith('/reset-password');
    expect(tokenFromFragment('token=abc_DEF-123')).toBe('abc_DEF-123');
    expect(tokenFromFragment(null)).toBeNull();
    expect(tokenFromFragment('token=' + 'a'.repeat(129))).toBeNull();
  });

  it('sets the new password with the token and offers the login', async () => {
    const { fixture, root } = create('token=abc_DEF-123');
    type(root, 'new', 'Yepyeni-Sifre-2027');
    type(root, 'confirm', 'Yepyeni-Sifre-2027');
    submit(root);

    const request = backend.expectOne('/api/v1/auth/password-reset/confirm');
    expect(request.request.body).toEqual({
      token: 'abc_DEF-123',
      newPassword: 'Yepyeni-Sifre-2027',
    });
    request.flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);

    expect(root.querySelector('[role="status"]')?.textContent).toContain(ui.resetPassword.success);
    expect(root.querySelector('a[href="/login"]')).not.toBeNull();
  });

  it('checks that both passwords match before calling the API', () => {
    const { fixture, root } = create('token=abc');
    type(root, 'new', 'Yepyeni-Sifre-2027');
    type(root, 'confirm', 'Baska-Sifre-2027');
    submit(root);
    fixture.detectChanges();

    backend.expectNone('/api/v1/auth/password-reset/confirm');
    expect(root.querySelector('#confirm-error')?.textContent).toContain(ui.resetPassword.mismatch);
  });

  it('offers a new link when the token is rejected', async () => {
    const { fixture, root } = create('token=abc');
    type(root, 'new', 'Yepyeni-Sifre-2027');
    type(root, 'confirm', 'Yepyeni-Sifre-2027');
    submit(root);
    backend
      .expectOne('/api/v1/auth/password-reset/confirm')
      .flush({ code: 'Auth.ResetTokenInvalid' }, { status: 422, statusText: 'Unprocessable' });
    await settle(fixture);

    const alert = root.querySelector('[role="alert"]')!;
    expect(alert.textContent).toContain(errorMessages['Auth.ResetTokenInvalid']);
    expect(alert.querySelector('a[href="/forgot-password"]')).not.toBeNull();
  });

  it('shows the server field error for a weak password', async () => {
    const { fixture, root } = create('token=abc');
    type(root, 'new', 'aaaaaaaaaa');
    type(root, 'confirm', 'aaaaaaaaaa');
    submit(root);
    backend
      .expectOne('/api/v1/auth/password-reset/confirm')
      .flush(
        { code: 'Validation.Failed', errors: { newPassword: ['Auth.PasswordTooWeak'] } },
        { status: 400, statusText: 'Bad Request' },
      );
    await settle(fixture);

    expect(root.querySelector('#new-error')?.textContent).toContain(
      errorMessages['Auth.PasswordTooWeak'],
    );
  });

  it('without a token it only offers a new link', () => {
    const { root } = create(null);

    expect(root.querySelector('form')).toBeNull();
    expect(root.querySelector('[role="alert"]')?.textContent).toContain(
      ui.resetPassword.missingToken,
    );
    expect(root.querySelector('a[href="/forgot-password"]')).not.toBeNull();
  });
});
