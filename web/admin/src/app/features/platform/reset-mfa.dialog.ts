import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';

import { problemMessage, toProblem } from '../../core/api/problem';
import { MfaResetReason, MfaService } from '../../core/auth/mfa.service';
import { interpolate, ui } from '../../core/i18n/tr';

export const RESET_MFA_TITLE_ID = 'reset-mfa-title';

export interface ResetMfaData {
  userId: string;
  /** Whose two-step verification it is, e.g. "Ayşe Yılmaz". */
  name: string;
}

/**
 * P5-01 (DEC-605): a Platform Admin removes a user's two-step verification after a lost phone. The reason is one of a fixed
 * list and goes to the audit log; the user is told by e-mail. Closes with true once it is done.
 */
@Component({
  selector: 'app-reset-mfa-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  template: `
    <form
      class="w-[min(28rem,calc(100vw-2rem))] rounded-lg border border-border bg-background p-6 shadow-lg"
      [formGroup]="form"
      (ngSubmit)="submit()"
    >
      <h2 [id]="titleId" class="text-lg font-semibold">{{ text.title }}</h2>
      <p id="reset-mfa-text" class="mt-2 text-sm text-muted-foreground">{{ intro }}</p>
      @if (error()) {
        <p
          role="alert"
          class="mt-3 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
        >
          {{ error() }}
        </p>
      }
      <div class="mt-4">
        <label class="field-label" for="reset-mfa-reason">{{ text.reason }}</label>
        <select
          id="reset-mfa-reason"
          class="input"
          formControlName="reason"
          aria-describedby="reset-mfa-text"
        >
          @for (reason of reasons; track reason) {
            <option [value]="reason">{{ text.reasons[reason] }}</option>
          }
        </select>
      </div>
      <div class="mt-6 flex flex-wrap justify-end gap-2">
        <button type="button" class="btn btn-secondary" (click)="ref.close(false)">
          {{ actions.cancel }}
        </button>
        <button type="submit" class="btn btn-destructive" [disabled]="busy()">
          {{ text.submit }}
        </button>
      </div>
    </form>
  `,
})
export class ResetMfaDialog {
  private readonly data = inject<ResetMfaData>(DIALOG_DATA);
  private readonly mfa = inject(MfaService);
  protected readonly ref = inject<DialogRef<boolean>>(DialogRef);

  protected readonly text = ui.mfa.reset;
  protected readonly actions = ui.actions;
  protected readonly titleId = RESET_MFA_TITLE_ID;
  protected readonly intro = interpolate(ui.mfa.reset.text, { name: this.data.name });
  protected readonly reasons: MfaResetReason[] = ['LostDevice', 'SuspectedCompromise', 'Other'];
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    reason: ['LostDevice' as MfaResetReason],
  });

  protected submit(): void {
    this.busy.set(true);
    this.error.set(null);
    this.mfa.reset(this.data.userId, this.form.getRawValue().reason).subscribe({
      next: () => this.ref.close(true),
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(problemMessage(toProblem(err)));
      },
    });
  }
}
