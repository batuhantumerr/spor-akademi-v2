import { ChangeDetectionStrategy, Component, ElementRef, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { LucideCircleAlert, LucideMailCheck, LucideMail } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1AuthPasswordResetRequestPost } from '../../api/functions';
import { problemMessage, toProblem } from '../../core/api/problem';
import { errorMessages, ui } from '../../core/i18n/tr';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';
import { LanguageSwitcherComponent } from '../../shared/ui/language-switcher.component';

/**
 * P2-F4 "Şifremi unuttum" (DEC-353). The API answers the same for every address, so the page always shows the same
 * confirmation and never says whether an account exists.
 */
@Component({
  selector: 'app-forgot-password-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    LanguageSwitcherComponent,
    ReactiveFormsModule,
    RouterLink,
    LucideCircleAlert,
    LucideMail,
    LucideMailCheck,
  ],
  template: `
    <main class="relative flex min-h-dvh items-center justify-center bg-background p-6">
      <!-- The language can be changed on every page reached from an e-mail link or a first sign-in (DEC-439). -->
      <div class="absolute top-4 right-4"><app-language-switcher /></div>
      <div class="card w-full max-w-md p-6 shadow-sm sm:p-8">
        <div class="mb-6 flex items-center gap-3">
          <div
            class="grid size-10 place-items-center rounded-lg border border-primary/20 bg-primary/10 text-primary"
          >
            <svg lucideMail class="size-5"></svg>
          </div>
          <h1 class="text-xl font-bold tracking-tight">{{ text.title }}</h1>
        </div>

        <!-- Always in the DOM, so screen readers announce the confirmation when it appears. -->
        <div role="status" aria-live="polite">
          @if (sent()) {
            <div
              class="flex items-start gap-2 rounded-lg border border-success/30 bg-success/10 p-3 text-sm"
            >
              <svg lucideMailCheck class="mt-0.5 size-4 shrink-0 text-success"></svg>
              <span>{{ text.sent }}</span>
            </div>
          }
        </div>
        @if (!sent()) {
          <p class="mb-4 text-sm text-muted-foreground">{{ text.subtitle }}</p>
          @if (error()) {
            <div
              role="alert"
              class="mb-4 flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
            >
              <svg lucideCircleAlert class="mt-0.5 size-4 shrink-0"></svg>
              <span>{{ error() }}</span>
            </div>
          }
          <form class="space-y-4" [formGroup]="form" (ngSubmit)="submit()" novalidate>
            <div>
              <label class="field-label" for="email">{{ text.email }}</label>
              <input
                id="email"
                type="email"
                class="input"
                formControlName="email"
                autocomplete="username"
                [attr.aria-invalid]="!!emailError()"
                [attr.aria-describedby]="emailError() ? 'email-error' : null"
              />
              @if (emailError(); as message) {
                <p id="email-error" class="field-error">{{ message }}</p>
              }
            </div>
            <button type="submit" class="btn btn-primary w-full" [disabled]="submitting()">
              {{ submitting() ? text.submitting : text.submit }}
            </button>
          </form>
        }

        <p class="mt-6 text-center text-sm">
          <a routerLink="/login" class="font-medium text-primary hover:underline">{{
            text.backToLogin
          }}</a>
        </p>
      </div>
    </main>
  `,
})
export class ForgotPasswordPage {
  private readonly api = inject(Api);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly text = ui.forgotPassword;
  protected readonly submitting = signal(false);
  protected readonly sent = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
  });

  protected emailError(): string | null {
    const control = this.form.controls.email;
    if (!control.invalid || !(control.touched || control.dirty)) {
      return null;
    }
    return control.hasError('required')
      ? errorMessages['Field.Required']
      : errorMessages['Email.Invalid'];
  }

  protected async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      focusFirstInvalid(this.host.nativeElement, [['email', true]]);
      return;
    }
    this.submitting.set(true);
    this.error.set(null);
    try {
      await this.api.invoke(apiV1AuthPasswordResetRequestPost, {
        body: { email: this.form.controls.email.value },
      });
      this.sent.set(true);
    } catch (error) {
      this.error.set(problemMessage(toProblem(error)));
    } finally {
      this.submitting.set(false);
    }
  }
}
