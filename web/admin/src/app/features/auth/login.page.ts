import { ChangeDetectionStrategy, Component, ElementRef, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  LucideCircleAlert,
  LucideEye,
  LucideEyeOff,
  LucideMoon,
  LucideShieldCheck,
  LucideSun,
  LucideTrophy,
} from '@lucide/angular';

import { toProblem, problemMessage } from '../../core/api/problem';
import { Observable, map, tap } from 'rxjs';

import {
  AuthService,
  CurrentUser,
  MfaChallenge,
  MfaEnrollmentStart,
  isMfaChallenge,
} from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ThemeService } from '../../core/theme/theme.service';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { LanguageSwitcherComponent } from '../../shared/ui/language-switcher.component';
import { MfaSetupComponent } from './mfa-setup.component';

@Component({
  selector: 'app-login-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    LucideCircleAlert,
    LucideEye,
    LucideEyeOff,
    LucideMoon,
    LucideShieldCheck,
    LucideSun,
    LucideTrophy,
    LanguageSwitcherComponent,
    MfaSetupComponent,
  ],
  template: `
    <main class="grid min-h-dvh lg:grid-cols-2">
      <section
        class="relative hidden overflow-hidden bg-gradient-to-br from-primary to-blue-900 p-12 text-white lg:flex lg:flex-col lg:justify-between"
      >
        <div
          class="pointer-events-none absolute -top-24 -left-24 size-96 rounded-full bg-white/10 blur-3xl"
        ></div>
        <div
          class="pointer-events-none absolute -right-32 -bottom-32 size-[28rem] rounded-full bg-sky-300/20 blur-3xl"
        ></div>
        <div class="relative flex items-center gap-3">
          <div
            class="grid size-11 place-items-center rounded-xl border border-white/30 bg-white/10"
          >
            <svg lucideTrophy class="size-6"></svg>
          </div>
          <span class="text-lg font-bold tracking-tight">{{ text.appName }}</span>
        </div>
        <div class="relative max-w-md">
          <h2 class="text-3xl leading-tight font-bold">{{ text.appTagline }}</h2>
          <ul class="mt-8 space-y-4">
            @for (item of text.login.highlights; track item) {
              <li class="flex items-start gap-3 text-white/90">
                <svg lucideShieldCheck class="mt-0.5 size-5 shrink-0 text-sky-200"></svg>
                <span>{{ item }}</span>
              </li>
            }
          </ul>
        </div>
        <p class="relative text-xs text-white/80">© {{ year }} {{ text.appName }}</p>
      </section>

      <section class="relative flex items-center justify-center p-6">
        <div class="absolute top-4 right-4 flex items-center gap-1">
          <app-language-switcher />
          <button
            type="button"
            class="btn btn-ghost btn-icon"
            [attr.aria-label]="text.theme.toggle"
            (click)="theme.toggle()"
          >
            @if (theme.isDark()) {
              <svg lucideSun></svg>
            } @else {
              <svg lucideMoon></svg>
            }
          </button>
        </div>

        <div class="w-full max-w-sm">
          <div class="mb-8 flex items-center gap-3 lg:hidden">
            <div
              class="grid size-10 place-items-center rounded-lg border border-primary/20 bg-primary/10 text-primary"
            >
              <svg lucideTrophy class="size-5"></svg>
            </div>
            <span class="text-lg font-bold">{{ text.appName }}</span>
          </div>
          @switch (step()) {
            @case ('code') {
              <h1 class="text-2xl font-bold tracking-tight">{{ mfa.codeTitle }}</h1>
              <p class="mt-1 text-sm text-muted-foreground">
                {{ useRecovery() ? mfa.recoverySubtitle : mfa.codeSubtitle }}
              </p>
            }
            @case ('enroll') {
              <h1 class="text-2xl font-bold tracking-tight">{{ mfa.enrollTitle }}</h1>
              <p class="mt-1 text-sm text-muted-foreground">{{ mfa.enrollRequired }}</p>
            }
            @default {
              <h1 class="text-2xl font-bold tracking-tight">{{ text.login.title }}</h1>
              <p class="mt-1 text-sm text-muted-foreground">{{ text.login.subtitle }}</p>
            }
          }

          @if (error() && step() !== 'enroll') {
            <div
              role="alert"
              class="mt-6 flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
            >
              <svg lucideCircleAlert class="mt-0.5 size-4 shrink-0"></svg>
              <span>{{ error() }}</span>
            </div>
          }

          @if (step() === 'code') {
            <form class="mt-6 space-y-4" [formGroup]="codeForm" (ngSubmit)="verify()" novalidate>
              <div>
                <label class="field-label" for="mfa-code">{{
                  useRecovery() ? mfa.recoveryCode : mfa.code
                }}</label>
                <input
                  id="mfa-code"
                  class="input font-mono text-lg tracking-widest"
                  formControlName="code"
                  autocomplete="one-time-code"
                  maxlength="32"
                  [attr.inputmode]="useRecovery() ? 'text' : 'numeric'"
                  [attr.aria-invalid]="codeRequired()"
                  [attr.aria-describedby]="codeRequired() ? 'mfa-code-error' : null"
                />
                @if (codeRequired()) {
                  <p id="mfa-code-error" class="field-error">{{ messages['Field.Required'] }}</p>
                }
              </div>
              <button type="submit" class="btn btn-primary w-full" [disabled]="submitting()">
                {{ submitting() ? mfa.submitting : mfa.submit }}
              </button>
              <div class="flex flex-wrap items-center justify-between gap-2 text-sm">
                <button
                  type="button"
                  class="font-medium text-primary hover:underline"
                  (click)="toggleRecovery()"
                >
                  {{ useRecovery() ? mfa.useApp : mfa.useRecovery }}
                </button>
                <button
                  type="button"
                  class="text-muted-foreground hover:text-foreground hover:underline"
                  (click)="backToCredentials()"
                >
                  {{ mfa.back }}
                </button>
              </div>
            </form>
          }

          @if (step() === 'enroll' && challenge(); as pending) {
            <div class="mt-6">
              <app-mfa-setup
                [begin]="beginEnrollment(pending)"
                [confirm]="confirmEnrollment(pending)"
                (failed)="enrollmentFailed($event.code)"
                (finished)="finishEnrollment()"
              />
            </div>
          }

          @if (step() === 'credentials') {
            <form class="mt-6 space-y-4" [formGroup]="form" (ngSubmit)="submit()" novalidate>
              <div>
                <label class="field-label" for="email">{{ text.login.email }}</label>
                <input
                  id="email"
                  type="email"
                  class="input"
                  formControlName="email"
                  autocomplete="username"
                  [attr.aria-invalid]="showError('email')"
                  [attr.aria-describedby]="showError('email') ? 'email-error' : null"
                />
                @if (showError('email')) {
                  <p id="email-error" class="field-error">
                    {{
                      form.controls.email.hasError('required')
                        ? messages['Field.Required']
                        : messages['Email.Invalid']
                    }}
                  </p>
                }
              </div>
              <div>
                <div class="flex items-baseline justify-between gap-2">
                  <label class="field-label" for="password">{{ text.login.password }}</label>
                  <a
                    routerLink="/forgot-password"
                    class="text-sm font-medium text-primary hover:underline"
                    >{{ text.login.forgotPassword }}</a
                  >
                </div>
                <div class="relative">
                  <input
                    id="password"
                    class="input pr-11"
                    formControlName="password"
                    autocomplete="current-password"
                    [type]="showPassword() ? 'text' : 'password'"
                    [attr.aria-invalid]="showError('password')"
                    [attr.aria-describedby]="showError('password') ? 'password-error' : null"
                  />
                  <!-- Toggle button: a fixed label plus aria-pressed announces the state. -->
                  <button
                    type="button"
                    class="absolute inset-y-0 right-0 grid w-10 place-items-center text-muted-foreground hover:text-foreground"
                    aria-controls="password"
                    [attr.aria-label]="text.login.showPassword"
                    [attr.aria-pressed]="showPassword()"
                    [attr.title]="
                      showPassword() ? text.login.hidePassword : text.login.showPassword
                    "
                    (click)="showPassword.set(!showPassword())"
                  >
                    @if (showPassword()) {
                      <svg lucideEyeOff class="size-4"></svg>
                    } @else {
                      <svg lucideEye class="size-4"></svg>
                    }
                  </button>
                </div>
                @if (showError('password')) {
                  <p id="password-error" class="field-error">{{ messages['Field.Required'] }}</p>
                }
              </div>
              <button type="submit" class="btn btn-primary w-full" [disabled]="submitting()">
                {{ submitting() ? text.login.submitting : text.login.submit }}
              </button>
            </form>
          }
          <!-- P4-06 (DEC-543): the privacy notice (KVKK md. 10), a static page served next to the app. -->
          <p class="mt-8 text-center text-xs">
            <a
              href="/aydinlatma-metni.html"
              class="text-muted-foreground underline underline-offset-4 hover:text-foreground"
              >{{ text.login.privacyNotice }}</a
            >
          </p>
        </div>
      </section>
    </main>
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly theme = inject(ThemeService);

  protected readonly text = ui;
  protected readonly mfa = ui.mfa.login;
  protected readonly messages = errorMessages;
  protected readonly year = new Date().getFullYear();
  protected readonly submitting = signal(false);
  protected readonly showPassword = signal(false);
  protected readonly error = signal<string | null>(
    this.route.snapshot.queryParamMap.has('expired') ? errorMessages['Auth.SessionExpired'] : null,
  );

  /** P5-01: the password first, then (when the account needs it) a code, or the enrolment of a role that must have MFA. */
  protected readonly step = signal<'credentials' | 'code' | 'enroll'>('credentials');
  protected readonly challenge = signal<MfaChallenge | null>(null);
  protected readonly useRecovery = signal(false);

  private readonly formBuilder = inject(FormBuilder);
  protected readonly form = this.formBuilder.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });
  protected readonly codeForm = this.formBuilder.nonNullable.group({
    code: ['', Validators.required],
  });
  /** Set once the first enrolment completed the login; the page waits behind the recovery codes. */
  private enrolled: CurrentUser | null = null;

  protected codeRequired(): boolean {
    const c = this.codeForm.controls.code;
    return c.invalid && (c.touched || c.dirty);
  }

  protected showError(control: 'email' | 'password'): boolean {
    const c = this.form.controls[control];
    return c.invalid && (c.touched || c.dirty);
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      focusFirstInvalid(this.host.nativeElement, [
        ['email', this.form.controls.email.invalid],
        ['password', this.form.controls.password.invalid],
      ]);
      return;
    }
    this.submitting.set(true);
    this.error.set(null);
    const { email, password } = this.form.getRawValue();
    this.auth.login(email, password).subscribe({
      next: (result) => {
        if (isMfaChallenge(result)) {
          this.submitting.set(false);
          this.challenge.set(result);
          this.codeForm.reset({ code: '' });
          this.useRecovery.set(false);
          this.step.set(result.purpose === 'Enroll' ? 'enroll' : 'code');
          this.focusCode();
          return;
        }
        this.enter(result);
      },
      error: (err: unknown) => {
        this.submitting.set(false);
        this.error.set(problemMessage(toProblem(err)));
      },
    });
  }

  protected verify(): void {
    const pending = this.challenge();
    if (this.codeForm.invalid || !pending) {
      this.codeForm.markAllAsTouched();
      return;
    }
    this.submitting.set(true);
    this.error.set(null);
    this.auth.verifyMfa(pending.mfaToken, this.codeForm.getRawValue().code).subscribe({
      next: (user) => this.enter(user),
      error: (err: unknown) => {
        this.submitting.set(false);
        const problem = toProblem(err);
        if (endsSecondStep(problem.code)) {
          this.backToCredentials(problemMessage(problem));
        } else {
          this.error.set(problemMessage(problem));
          this.codeForm.reset({ code: '' });
        }
      },
    });
  }

  protected toggleRecovery(): void {
    this.useRecovery.set(!this.useRecovery());
    this.codeForm.reset({ code: '' });
    this.error.set(null);
    this.focusCode();
  }

  /** The code field replaces the password form: move focus to it once it is rendered, so keyboard and screen reader users land on it. */
  private focusCode(): void {
    setTimeout(() => this.host.nativeElement.querySelector<HTMLInputElement>('#mfa-code')?.focus());
  }

  protected backToCredentials(message: string | null = null): void {
    this.challenge.set(null);
    this.form.controls.password.reset('');
    this.error.set(message);
    this.step.set('credentials');
  }

  protected beginEnrollment(pending: MfaChallenge): () => Observable<MfaEnrollmentStart> {
    return () => this.auth.startMfaEnrollment(pending.mfaToken);
  }

  /** Completes the login behind the recovery codes: the session exists, but the page waits until they are saved. */
  protected confirmEnrollment(pending: MfaChallenge): (code: string) => Observable<string[]> {
    return (code) =>
      this.auth.completeMfaEnrollment(pending.mfaToken, code).pipe(
        tap((result) => (this.enrolled = result.user)),
        map((result) => result.recoveryCodes),
      );
  }

  protected enrollmentFailed(code: string): void {
    if (endsSecondStep(code)) {
      this.backToCredentials(errorMessages[code] ?? null);
    }
  }

  protected finishEnrollment(): void {
    if (this.enrolled) {
      this.enter(this.enrolled);
    }
  }

  private enter(user: CurrentUser): void {
    const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
    const target = user.mustChangePassword
      ? '/change-password'
      : returnUrl?.startsWith('/') && !returnUrl.startsWith('//')
        ? returnUrl
        : this.auth.homeFor(user.role);
    void this.router.navigateByUrl(target);
  }
}

/** The sign-in step cannot go on: the token is gone, or the account is locked. The user starts over. */
function endsSecondStep(code: string): boolean {
  return code === 'Mfa.ChallengeInvalid' || code === 'Auth.AccountLocked';
}
