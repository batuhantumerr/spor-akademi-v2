import { DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';

import { problemMessage, toProblem } from '../../core/api/problem';
import { MfaService } from '../../core/auth/mfa.service';
import { errorMessages, ui } from '../../core/i18n/tr';
import { MfaRecoveryCodesComponent } from '../auth/mfa-recovery-codes.component';

export const REGENERATE_CODES_TITLE_ID = 'regenerate-codes-title';

/**
 * V1-03 (DEC-687): replaces the recovery codes after a current code from the authenticator app, then shows the new codes
 * once. Closes with true when the user confirmed they saved them (the codes exist on the server from the moment they are
 * shown, so cancelling before that changes nothing; closing after is still a regeneration).
 */
@Component({
  selector: 'app-regenerate-codes-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MfaRecoveryCodesComponent],
  template: `
    <div
      class="w-[min(32rem,calc(100vw-2rem))] rounded-lg border border-border bg-background p-6 shadow-lg"
    >
      @if (codes(); as list) {
        <app-mfa-recovery-codes [codes]="list" (finished)="ref.close(true)" />
      } @else {
        <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
          <h2 [id]="titleId" class="text-lg font-semibold">{{ text.regenerateTitle }}</h2>
          <p id="regenerate-codes-text" class="mt-2 text-sm text-muted-foreground">
            {{ text.regenerateText }}
          </p>
          @if (error()) {
            <p
              role="alert"
              class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
            >
              {{ error() }}
            </p>
          }
          <div class="mt-4">
            <label class="field-label" for="regenerate-codes-code">{{ setup.code }}</label>
            <input
              id="regenerate-codes-code"
              class="input font-mono text-lg tracking-widest"
              formControlName="code"
              inputmode="numeric"
              autocomplete="one-time-code"
              maxlength="12"
              [attr.aria-invalid]="showRequired()"
              [attr.aria-describedby]="
                showRequired()
                  ? 'regenerate-codes-text regenerate-codes-code-error'
                  : 'regenerate-codes-text'
              "
            />
            @if (showRequired()) {
              <p id="regenerate-codes-code-error" class="field-error">
                {{ messages['Field.Required'] }}
              </p>
            }
          </div>
          <div class="mt-6 flex flex-wrap justify-end gap-2">
            <button type="button" class="btn btn-secondary" (click)="ref.close(false)">
              {{ actions.cancel }}
            </button>
            <button type="submit" class="btn btn-primary" [disabled]="busy()">
              {{ text.regenerateConfirm }}
            </button>
          </div>
        </form>
      }
    </div>
  `,
})
export class RegenerateCodesDialog {
  private readonly mfa = inject(MfaService);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);

  protected readonly text = ui.mfa.settings;
  protected readonly setup = ui.mfa.setup;
  protected readonly actions = ui.actions;
  protected readonly messages = errorMessages;
  protected readonly titleId = REGENERATE_CODES_TITLE_ID;
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly codes = signal<string[] | null>(null);
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
    this.mfa.regenerateRecoveryCodes(this.form.getRawValue().code).subscribe({
      next: (codes) => {
        // The old codes are gone: Escape or a click outside must not throw the new ones away unread.
        this.ref.disableClose = true;
        this.codes.set(codes);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.form.controls.code.reset('');
        this.error.set(problemMessage(toProblem(err)));
      },
    });
  }
}
