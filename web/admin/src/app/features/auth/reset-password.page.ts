import { Location } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { LucideCircleAlert, LucideCircleCheck, LucideKeyRound } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1AuthPasswordResetConfirmPost } from '../../api/functions';
import { fieldErrors, problemMessage, toProblem } from '../../core/api/problem';
import { AuthService } from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';
import { focusFirstInvalid } from '../../shared/forms/focus-first-invalid';

function matchingPasswords(group: AbstractControl): ValidationErrors | null {
  return group.get('newPassword')?.value === group.get('confirm')?.value
    ? null
    : { mismatch: true };
}

/** Reads <c>token</c> from a URL fragment such as <c>token=abc</c>. */
export function tokenFromFragment(fragment: string | null): string | null {
  const token = new URLSearchParams(fragment ?? '').get('token');
  return token && token.length <= 128 ? token : null;
}

/**
 * P2-F4 "Yeni şifre belirle" for reset and welcome links (DEC-351, DEC-352). The token comes in the URL fragment,
 * which browsers never send to a server; the page removes it from the address bar and history at once.
 */
@Component({
  selector: 'app-reset-password-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, LucideCircleAlert, LucideCircleCheck, LucideKeyRound],
  template: `
    <div class="flex min-h-dvh items-center justify-center bg-background p-6">
      <div class="card w-full max-w-md p-6 shadow-sm sm:p-8">
        <div class="mb-6 flex items-center gap-3">
          <div
            class="grid size-10 place-items-center rounded-lg border border-primary/20 bg-primary/10 text-primary"
          >
            <svg lucideKeyRound class="size-5"></svg>
          </div>
          <h1 class="text-xl font-bold tracking-tight">{{ text.title }}</h1>
        </div>

        <!-- Always in the DOM, so screen readers announce the success when it appears. -->
        <div role="status" aria-live="polite">
          @if (done()) {
            <div
              class="flex items-start gap-2 rounded-lg border border-success/30 bg-success/10 p-3 text-sm"
            >
              <svg lucideCircleCheck class="mt-0.5 size-4 shrink-0 text-success"></svg>
              <span>{{ text.success }}</span>
            </div>
          }
        </div>
        @if (done()) {
          <a routerLink="/login" class="btn btn-primary mt-6 w-full">{{ text.toLogin }}</a>
        } @else if (!token) {
          <div
            role="alert"
            class="flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
          >
            <svg lucideCircleAlert class="mt-0.5 size-4 shrink-0"></svg>
            <span>{{ text.missingToken }}</span>
          </div>
          <a routerLink="/forgot-password" class="btn btn-primary mt-6 w-full">{{
            text.requestNew
          }}</a>
        } @else {
          <p class="mb-4 text-sm text-muted-foreground">{{ text.subtitle }}</p>
          @if (error()) {
            <div
              role="alert"
              class="mb-4 flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
            >
              <svg lucideCircleAlert class="mt-0.5 size-4 shrink-0"></svg>
              <span
                >{{ error() }}
                @if (tokenRejected()) {
                  <a routerLink="/forgot-password" class="font-medium underline">{{
                    text.requestNew
                  }}</a>
                }
              </span>
            </div>
          }
          <form class="space-y-4" [formGroup]="form" (ngSubmit)="submit()" novalidate>
            <div>
              <label class="field-label" for="new">{{ text.next }}</label>
              <input
                id="new"
                type="password"
                class="input"
                formControlName="newPassword"
                autocomplete="new-password"
                [attr.aria-invalid]="!!fieldError('newPassword')"
                [attr.aria-describedby]="
                  fieldError('newPassword') ? 'new-hint new-error' : 'new-hint'
                "
              />
              <p id="new-hint" class="mt-1.5 text-xs text-muted-foreground">{{ text.hint }}</p>
              @if (fieldError('newPassword'); as message) {
                <p id="new-error" class="field-error">{{ message }}</p>
              }
            </div>
            <div>
              <label class="field-label" for="confirm">{{ text.confirm }}</label>
              <input
                id="confirm"
                type="password"
                class="input"
                formControlName="confirm"
                autocomplete="new-password"
                [attr.aria-invalid]="!!fieldError('confirm')"
                [attr.aria-describedby]="fieldError('confirm') ? 'confirm-error' : null"
              />
              @if (fieldError('confirm'); as message) {
                <p id="confirm-error" class="field-error">{{ message }}</p>
              }
            </div>
            <button type="submit" class="btn btn-primary w-full" [disabled]="submitting()">
              {{ submitting() ? text.submitting : text.submit }}
            </button>
          </form>
        }
      </div>
    </div>
  `,
})
export class ResetPasswordPage {
  private readonly api = inject(Api);
  private readonly auth = inject(AuthService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly text = ui.resetPassword;
  protected readonly token = tokenFromFragment(inject(ActivatedRoute).snapshot.fragment);
  protected readonly submitting = signal(false);
  protected readonly done = signal(false);
  protected readonly tokenRejected = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly serverErrors = signal<Record<string, string>>({});

  protected readonly form = inject(FormBuilder).nonNullable.group(
    {
      newPassword: ['', [Validators.required, Validators.minLength(10)]],
      confirm: ['', Validators.required],
    },
    { validators: matchingPasswords },
  );

  constructor() {
    // Keep the single-use token out of the address bar, history and any later Referer.
    inject(Location).replaceState('/reset-password');
  }

  protected fieldError(field: 'newPassword' | 'confirm'): string | null {
    const server = this.serverErrors()[field];
    if (server) {
      return server;
    }
    const control = this.form.controls[field];
    if (!control.touched) {
      return null;
    }
    if (control.hasError('required')) {
      return errorMessages['Field.Required'];
    }
    if (field === 'newPassword' && control.hasError('minlength')) {
      return errorMessages['Auth.PasswordTooWeak'];
    }
    if (field === 'confirm' && this.form.hasError('mismatch')) {
      return this.text.mismatch;
    }
    return null;
  }

  protected async submit(): Promise<void> {
    if (this.form.invalid || !this.token) {
      this.form.markAllAsTouched();
      focusFirstInvalid(this.host.nativeElement, [
        ['new', !!this.fieldError('newPassword')],
        ['confirm', !!this.fieldError('confirm')],
      ]);
      return;
    }
    this.submitting.set(true);
    this.error.set(null);
    this.tokenRejected.set(false);
    this.serverErrors.set({});
    try {
      await this.api.invoke(apiV1AuthPasswordResetConfirmPost, {
        body: { token: this.token, newPassword: this.form.controls.newPassword.value },
      });
      // Every session of the account ended on the server; drop this tab's stale one too.
      this.auth.forgetSession();
      this.done.set(true);
    } catch (err) {
      const problem = toProblem(err);
      this.serverErrors.set(fieldErrors(problem));
      this.tokenRejected.set(problem.code === 'Auth.ResetTokenInvalid');
      this.error.set(problemMessage(problem));
      focusFirstInvalid(this.host.nativeElement, [['new', !!this.serverErrors()['newPassword']]]);
    } finally {
      this.submitting.set(false);
    }
  }
}
