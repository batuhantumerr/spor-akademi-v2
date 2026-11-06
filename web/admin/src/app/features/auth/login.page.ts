import { ChangeDetectionStrategy, Component, ElementRef, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
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
import { AuthService } from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';
import { ThemeService } from '../../core/theme/theme.service';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';

@Component({
  selector: 'app-login-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    LucideCircleAlert,
    LucideEye,
    LucideEyeOff,
    LucideMoon,
    LucideShieldCheck,
    LucideSun,
    LucideTrophy,
  ],
  template: `
    <div class="grid min-h-dvh lg:grid-cols-2">
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
        <button
          type="button"
          class="btn btn-ghost btn-icon absolute top-4 right-4"
          [attr.aria-label]="text.theme.toggle"
          (click)="theme.toggle()"
        >
          @if (theme.isDark()) {
            <svg lucideSun></svg>
          } @else {
            <svg lucideMoon></svg>
          }
        </button>

        <div class="w-full max-w-sm">
          <div class="mb-8 flex items-center gap-3 lg:hidden">
            <div
              class="grid size-10 place-items-center rounded-lg border border-primary/20 bg-primary/10 text-primary"
            >
              <svg lucideTrophy class="size-5"></svg>
            </div>
            <span class="text-lg font-bold">{{ text.appName }}</span>
          </div>
          <h1 class="text-2xl font-bold tracking-tight">{{ text.login.title }}</h1>
          <p class="mt-1 text-sm text-muted-foreground">{{ text.login.subtitle }}</p>

          @if (error()) {
            <div
              role="alert"
              class="mt-6 flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
            >
              <svg lucideCircleAlert class="mt-0.5 size-4 shrink-0"></svg>
              <span>{{ error() }}</span>
            </div>
          }

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
              <label class="field-label" for="password">{{ text.login.password }}</label>
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
                  [attr.title]="showPassword() ? text.login.hidePassword : text.login.showPassword"
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
        </div>
      </section>
    </div>
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly theme = inject(ThemeService);

  protected readonly text = ui;
  protected readonly messages = errorMessages;
  protected readonly year = new Date().getFullYear();
  protected readonly submitting = signal(false);
  protected readonly showPassword = signal(false);
  protected readonly error = signal<string | null>(
    this.route.snapshot.queryParamMap.has('expired') ? errorMessages['Auth.SessionExpired'] : null,
  );

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });

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
      next: (user) => {
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        const target = user.mustChangePassword
          ? '/change-password'
          : returnUrl?.startsWith('/') && !returnUrl.startsWith('//')
            ? returnUrl
            : this.auth.homeFor(user.role);
        void this.router.navigateByUrl(target);
      },
      error: (err: unknown) => {
        this.submitting.set(false);
        this.error.set(problemMessage(toProblem(err)));
      },
    });
  }
}
