import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  Injectable,
  OnDestroy,
  inject,
  signal,
} from '@angular/core';
import { LucideCopy, LucideKeyRound, LucideTriangleAlert } from '@lucide/angular';
import { firstValueFrom } from 'rxjs';

import { interpolate, ui } from '../../core/i18n/tr';

export interface TemporaryPasswordData {
  /** Whose password it is, e.g. "Ayşe Yılmaz". */
  name: string;
  password: string;
}

const TITLE_ID = 'temporary-password-title';
const WARNING_ID = 'temporary-password-warning';

/**
 * Shows a one-time temporary password (US-02, US-04). The value lives only in this dialog: it is
 * never logged or stored, and it is wiped from memory when the dialog closes.
 */
@Component({
  selector: 'app-temporary-password-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideCopy, LucideKeyRound, LucideTriangleAlert],
  template: `
    <div
      class="w-[min(28rem,calc(100vw-2rem))] rounded-lg border border-border bg-background p-6 shadow-lg"
    >
      <div class="flex items-center gap-3">
        <div
          class="grid size-10 shrink-0 place-items-center rounded-lg border border-primary/20 bg-primary/10 text-primary"
        >
          <svg lucideKeyRound class="size-5"></svg>
        </div>
        <h2 [id]="titleId" class="text-lg font-semibold">{{ text.title }}</h2>
      </div>
      <p class="mt-4 text-sm text-muted-foreground">{{ intro }}</p>
      <div class="mt-2 flex items-stretch gap-2">
        <label class="sr-only" for="temporary-password">{{ text.label }}</label>
        <input
          id="temporary-password"
          class="input min-w-0 flex-1 font-mono text-base tracking-wider"
          readonly
          autocomplete="off"
          spellcheck="false"
          [value]="password()"
          (focus)="select($event)"
        />
        <button type="button" class="btn btn-secondary shrink-0" (click)="copy()">
          <svg lucideCopy></svg>{{ text.copy }}
        </button>
      </div>
      <p class="mt-2 min-h-5 text-xs" role="status" [class.text-success]="copied() === true">
        @if (copied() === true) {
          {{ text.copied }}
        } @else if (copied() === false) {
          <span class="text-destructive">{{ text.copyFailed }}</span>
        }
      </p>
      <p
        [id]="warningId"
        class="mt-3 flex items-start gap-2 rounded-lg border border-warning/30 bg-warning/10 p-3 text-sm text-warning"
      >
        <svg lucideTriangleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true"></svg>
        <span>{{ text.warning }}</span>
      </p>
      <div class="mt-6 flex justify-end">
        <button type="button" class="btn btn-primary" (click)="close()">{{ text.done }}</button>
      </div>
    </div>
  `,
})
export class TemporaryPasswordDialog implements OnDestroy {
  private readonly data = inject<TemporaryPasswordData>(DIALOG_DATA);
  private readonly ref = inject<DialogRef<void>>(DialogRef);

  protected readonly text = ui.temporaryPassword;
  protected readonly titleId = TITLE_ID;
  protected readonly warningId = WARNING_ID;
  protected readonly intro = interpolate(ui.temporaryPassword.intro, { name: this.data.name });
  /** Readable by tests to prove the value is wiped on close. */
  readonly password = signal(this.data.password);
  protected readonly copied = signal<boolean | null>(null);

  constructor() {
    // Drop the caller's reference too; the dialog's copy is the only one left.
    this.data.password = '';
  }

  protected select(event: Event): void {
    (event.target as HTMLInputElement).select();
  }

  protected async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.password());
      this.copied.set(true);
    } catch {
      this.copied.set(false);
    }
  }

  protected close(): void {
    this.ref.close();
  }

  ngOnDestroy(): void {
    this.password.set('');
  }
}

/** Opens the one-time password dialog and resolves when the user has closed it. */
@Injectable({ providedIn: 'root' })
export class TemporaryPasswordService {
  private readonly dialog = inject(Dialog);

  async show(data: TemporaryPasswordData): Promise<void> {
    const ref = this.dialog.open<void, TemporaryPasswordData>(TemporaryPasswordDialog, {
      data,
      role: 'alertdialog',
      ariaLabelledBy: TITLE_ID,
      ariaDescribedBy: WARNING_ID,
      // Only the explicit button closes it, so a stray click or Escape cannot lose the password.
      disableClose: true,
      backdropClass: ['bg-slate-900/60'],
    });
    await firstValueFrom(ref.closed);
  }
}
