import { DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { problemMessage, toProblem } from '../../core/api/problem';
import { MfaService } from '../../core/auth/mfa.service';
import { errorMessages, ui } from '../../core/i18n/tr';

export const DISABLE_MFA_TITLE_ID = 'disable-mfa-title';

/** P5-01: turning two-step verification off asks for a current code (or a recovery code). Closes with true once it is off. */
@Component({
  selector: 'app-disable-mfa-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  template: `
    <form
      class="w-[min(28rem,calc(100vw-2rem))] rounded-lg border border-border bg-background p-6 shadow-lg"
      [formGroup]="form"
      (ngSubmit)="submit()"
      novalidate
    >
      <h2 [id]="titleId" class="text-lg font-semibold">{{ text.disableTitle }}</h2>
      <p id="disable-mfa-text" class="mt-2 text-sm text-muted-foreground">{{ text.disableText }}</p>
      @if (error()) {
        <p
          role="alert"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }
      <div class="mt-4">
        <label class="field-label" for="disable-mfa-code">{{ setup.code }}</label>
        <input
          id="disable-mfa-code"
          class="input font-mono text-lg tracking-widest"
          formControlName="code"
          autocomplete="one-time-code"
          maxlength="32"
          [attr.aria-invalid]="showRequired()"
          [attr.aria-describedby]="
            showRequired() ? 'disable-mfa-text disable-mfa-code-error' : 'disable-mfa-text'
          "
        />
        @if (showRequired()) {
          <p id="disable-mfa-code-error" class="field-error">{{ messages['Field.Required'] }}</p>
        }
      </div>
      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close(false)">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-destructive" [disabled]="busy()">
          {{ text.disableConfirm }}
        </button>
      </div>
    </form>
  `,
})
export class DisableMfaDialog {
  private readonly mfa = inject(MfaService);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);

  protected readonly text = ui.mfa.settings;
  protected readonly setup = ui.mfa.setup;
  protected readonly actions = ui.actions;
  protected readonly messages = errorMessages;
  protected readonly titleId = DISABLE_MFA_TITLE_ID;
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    code: ['', Validators.required],
  });

  protected showRequired(): boolean {
    const control = this.form.controls.code;
    return control.invalid && (control.touched || control.dirty);
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.mfa.disable(this.form.getRawValue().code).subscribe({
      next: () => this.ref.close(true),
      error: (err: unknown) => {
        this.busy.set(false);
        this.form.controls.code.reset('');
        this.error.set(problemMessage(toProblem(err)));
      },
    });
  }
}
