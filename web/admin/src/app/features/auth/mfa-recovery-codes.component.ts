import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { LucideCopy, LucideDownload } from '@lucide/angular';

import { ui } from '../../core/i18n/tr';

/**
 * The recovery codes of two-step verification, shown once (P5-01, V1-03): a list to read, copy or download, and a
 * confirmation that they were saved before the host lets the user go on. Shared by the first setup and by the
 * regeneration of the codes.
 */
@Component({
  selector: 'app-mfa-recovery-codes',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideCopy, LucideDownload],
  template: `
    <section aria-labelledby="mfa-codes-title">
      <h2 id="mfa-codes-title" #codesTitle tabindex="-1" class="text-lg font-semibold">
        {{ text.codesTitle }}
      </h2>
      <p class="mt-2 text-sm text-muted-foreground">{{ text.codesIntro }}</p>
      <ul
        class="mt-4 grid grid-cols-1 gap-2 rounded-lg border border-border bg-muted/40 p-4 font-mono text-sm sm:grid-cols-2"
        [attr.aria-label]="text.codesList"
      >
        @for (code of codes(); track code) {
          <li>{{ code }}</li>
        }
      </ul>
      <div class="mt-3 flex flex-wrap gap-2">
        <button type="button" class="btn btn-secondary" (click)="copyCodes()">
          <svg lucideCopy aria-hidden="true"></svg>{{ text.codesCopy }}
        </button>
        <button type="button" class="btn btn-secondary" (click)="downloadCodes()">
          <svg lucideDownload aria-hidden="true"></svg>{{ text.codesDownload }}
        </button>
      </div>
      <p class="mt-2 min-h-5 text-xs" role="status">
        @if (copied() === true) {
          <span class="text-success">{{ text.copied }}</span>
        } @else if (copied() === false) {
          <span class="text-destructive">{{ text.copyFailed }}</span>
        }
      </p>
      <label class="mt-4 flex items-start gap-2 text-sm" for="mfa-codes-saved">
        <input
          id="mfa-codes-saved"
          type="checkbox"
          class="mt-0.5 size-4"
          [checked]="saved()"
          (change)="saved.set(!saved())"
        />
        <span>{{ text.saved }}</span>
      </label>
      <button
        type="button"
        class="btn btn-primary mt-4 w-full"
        [disabled]="!saved()"
        (click)="finished.emit()"
      >
        {{ text.continue }}
      </button>
    </section>
  `,
})
export class MfaRecoveryCodesComponent {
  readonly codes = input.required<string[]>();
  /** The user confirmed that they saved the codes. */
  readonly finished = output<void>();

  protected readonly text = ui.mfa.setup;
  protected readonly copied = signal<boolean | null>(null);
  protected readonly saved = signal(false);
  private readonly codesTitle = viewChild<ElementRef<HTMLElement>>('codesTitle');

  constructor() {
    // The codes appear in place of a form: move focus to their heading so a screen reader announces them.
    effect(() => this.codesTitle()?.nativeElement.focus());
  }

  protected async copyCodes(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.codes().join('\n'));
      this.copied.set(true);
    } catch {
      this.copied.set(false);
    }
  }

  protected downloadCodes(): void {
    const url = URL.createObjectURL(
      new Blob([this.codes().join('\n') + '\n'], { type: 'text/plain' }),
    );
    const link = document.createElement('a');
    link.href = url;
    link.download = this.text.codesFile;
    link.click();
    URL.revokeObjectURL(url);
  }
}
