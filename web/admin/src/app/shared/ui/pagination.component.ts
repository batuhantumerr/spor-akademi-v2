import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { LucideChevronLeft, LucideChevronRight } from '@lucide/angular';

import { formatNumber } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';

/** Offset paging footer (appendix D §1.6). */
@Component({
  selector: 'app-pagination',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideChevronLeft, LucideChevronRight],
  template: `
    @if (totalPages() > 1) {
      <nav
        class="flex items-center justify-between gap-3 border-t border-border px-4 py-3 text-sm"
        [attr.aria-label]="text.pagination"
      >
        <p class="text-muted-foreground">{{ summary() }}</p>
        <div class="flex items-center gap-1">
          <button
            type="button"
            class="btn btn-secondary btn-sm btn-icon"
            [disabled]="disabled() || page() <= 1"
            [attr.aria-label]="text.previousPage"
            (click)="pageChange.emit(page() - 1)"
          >
            <svg lucideChevronLeft></svg>
          </button>
          <span class="px-2 font-mono text-xs">{{ position() }}</span>
          <button
            type="button"
            class="btn btn-secondary btn-sm btn-icon"
            [disabled]="disabled() || page() >= totalPages()"
            [attr.aria-label]="text.nextPage"
            (click)="pageChange.emit(page() + 1)"
          >
            <svg lucideChevronRight></svg>
          </button>
        </div>
      </nav>
    }
  `,
})
export class PaginationComponent {
  readonly page = input.required<number>();
  readonly totalPages = input.required<number>();
  readonly totalCount = input.required<number>();
  /** Blocks paging while a request is in flight. */
  readonly disabled = input(false);
  readonly pageChange = output<number>();

  protected readonly text = ui.common;
  protected readonly summary = computed(() =>
    interpolate(ui.common.totalRecords, { count: formatNumber(this.totalCount()) }),
  );
  protected readonly position = computed(() =>
    interpolate(ui.common.pageOf, { page: this.page(), total: this.totalPages() }),
  );
}
