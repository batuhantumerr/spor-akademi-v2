import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { ui } from '../../core/i18n/tr';

/**
 * Shown after a 409 Concurrency.Conflict while the user's edits are kept: the record changed on
 * the server, and the user decides whether to re-apply their edits on top of the fresh version or
 * to take the server's version. Nothing is discarded silently.
 */
@Component({
  selector: 'app-conflict-notice',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      role="alert"
      tabindex="-1"
      class="rounded-lg border border-warning/30 bg-warning/10 p-3 text-sm text-warning"
    >
      <p class="font-semibold">{{ text.title }}</p>
      @if (loadError(); as message) {
        <p class="mt-1 text-destructive">{{ message }}</p>
        <button type="button" class="btn btn-secondary btn-sm mt-2" (click)="retry.emit()">
          {{ common.retry }}
        </button>
      } @else if (!ready()) {
        <p class="mt-1">{{ text.loading }}</p>
      } @else {
        <p class="mt-1">{{ text.help }}</p>
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
}
