import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  inject,
  input,
  output,
} from '@angular/core';

import { ui } from '../../core/i18n/tr';

let nextId = 0;

/**
 * Shown after a 409 Concurrency.Conflict while the user's edits are kept: the record changed on
 * the server, and the user decides whether to re-apply their edits on top of the fresh version or
 * to take the server's version. Nothing is discarded silently.
 *
 * Focus moves to the notice when it appears; the loading / error / ready text sits in a polite
 * live region so the change is announced without stealing focus again. Amber text on the amber
 * tint is 4.86:1 (light) and 6.31:1 (dark), above WCAG AA (docs/ui-tema.md).
 */
@Component({
  selector: 'app-conflict-notice',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      tabindex="-1"
      role="group"
      [attr.aria-labelledby]="titleId"
      class="conflict-notice rounded-lg border border-warning/30 bg-warning/10 p-3 text-sm text-warning outline-none focus-visible:ring-2 focus-visible:ring-warning"
    >
      <p [id]="titleId" class="font-semibold">{{ text.title }}</p>
      <div aria-live="polite">
        @if (loadError(); as message) {
          <p class="mt-1 text-destructive">{{ message }}</p>
        } @else if (!ready()) {
          <p class="mt-1">{{ text.loading }}</p>
        } @else {
          <p class="mt-1">{{ text.help }}</p>
        }
      </div>
      @if (loadError()) {
        <button type="button" class="btn btn-secondary btn-sm mt-2" (click)="retry.emit()">
          {{ common.retry }}
        </button>
      } @else if (ready()) {
        <div class="mt-2 flex flex-wrap gap-2">
          <button
            type="button"
            class="btn btn-primary btn-sm"
            [disabled]="busy()"
            (click)="applyMine.emit()"
          >
            {{ text.applyMine }}
          </button>
          <button
            type="button"
            class="btn btn-secondary btn-sm"
            [disabled]="busy()"
            (click)="loadServer.emit()"
          >
            {{ text.loadServer }}
          </button>
        </div>
      }
    </div>
  `,
})
export class ConflictNoticeComponent {
  /** The fresh server version has been loaded. */
  readonly ready = input(false);
  readonly loadError = input<string | null>(null);
  readonly busy = input(false);
  readonly applyMine = output<void>();
  readonly loadServer = output<void>();
  readonly retry = output<void>();

  protected readonly text = ui.conflict;
  protected readonly common = ui.common;
  protected readonly titleId = `conflict-title-${++nextId}`;

  constructor() {
    // The notice replaces the save the user just attempted; bring them to it.
    const host = inject<ElementRef<HTMLElement>>(ElementRef);
    afterNextRender(() =>
      host.nativeElement.querySelector<HTMLElement>('.conflict-notice')?.focus(),
    );
  }
}
