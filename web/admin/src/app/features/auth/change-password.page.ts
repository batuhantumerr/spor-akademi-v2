import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { Router } from '@angular/router';
import { LucideCircleAlert, LucideKeyRound } from '@lucide/angular';

import { fieldErrors, problemMessage, toProblem } from '../../core/api/problem';
import { AuthService } from '../../core/auth/auth.service';
import { errorMessages, ui } from '../../core/i18n/tr';

function matchingPasswords(group: AbstractControl): ValidationErrors | null {
  return group.get('newPassword')?.value === group.get('confirm')?.value
    ? null
    : { mismatch: true };
}

/** US-05: first-login (and voluntary) password change. */
@Component({
  selector: 'app-change-password-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, LucideCircleAlert, LucideKeyRound],
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
        @if (auth.mustChangePassword()) {
          <p
            class="mb-4 rounded-lg border border-warning/30 bg-warning/10 p-3 text-sm text-warning"
          >
            {{ text.requiredNotice }}
          </p>
        }
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
            <label class="field-label" for="current">{{ text.current }}</label>
            <input
              id="current"
              type="password"
              class="input"
              formControlName="currentPassword"
              autocomplete="current-password"
            />
            @if (serverErrors()['currentPassword']; as message) {
              <p class="field-error">{{ message }}</p>
            }
          </div>
          <div>
            <label class="field-label" for="new">{{ text.next }}</label>
            <input
              id="new"
              type="password"
              class="input"
              formControlName="newPassword"
              autocomplete="new-password"
              [attr.aria-invalid]="
                form.controls.newPassword.invalid && form.controls.newPassword.touched
              "
            />
            <p class="mt-1.5 text-xs text-muted-foreground">{{ text.hint }}</p>
            @if (serverErrors()['newPassword']; as message) {
              <p class="field-error">{{ message }}</p>
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
            />
            @if (form.hasError('mismatch') && form.controls.confirm.touched) {
              <p class="field-error">{{ text.mismatch }}</p>
            }
          </div>
          <button type="submit" class="btn btn-primary w-full" [disabled]="submitting()">
            {{ text.submit }}
          </button>
        </form>
      </div>
    </div>
  `,
})
export class ChangePasswordPage {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly text = ui.changePassword;
  protected readonly submitting = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly serverErrors = signal<Record<string, string>>({});

  protected readonly form = inject(FormBuilder).nonNullable.group(
    {
      currentPassword: ['', Validators.required],
      newPassword: ['', [Validators.required, Validators.minLength(10)]],
      confirm: ['', Validators.required],
    },
    { validators: matchingPasswords },
  );

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      if (this.form.controls.newPassword.hasError('minlength')) {
        this.error.set(errorMessages['Auth.PasswordTooWeak']);
      }
      return;
    }
    this.submitting.set(true);
    this.error.set(null);
    this.serverErrors.set({});
    const { currentPassword, newPassword } = this.form.getRawValue();
    this.auth.changePassword(currentPassword, newPassword).subscribe({
      next: (user) => void this.router.navigateByUrl(this.auth.homeFor(user.role)),
      error: (err: unknown) => {
        this.submitting.set(false);
        const problem = toProblem(err);
        this.serverErrors.set(fieldErrors(problem));
        this.error.set(problemMessage(problem));
      },
    });
  }
}
