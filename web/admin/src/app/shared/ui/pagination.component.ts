import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { LucideChevronLeft, LucideChevronRight } from '@lucide/angular';

import { formatNumber } from '../../core/format/format';

/** Offset paging footer (appendix D §1.6). */
@Component({
  selector: 'app-pagination',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LucideChevronLeft, LucideChevronRight],
  template: `
    @if (totalPages() > 1) {
      <nav
        class="flex items-center justify-between gap-3 border-t border-border px-4 py-3 text-sm"
        aria-label="Sayfalama"
      >
        <p class="text-muted-foreground">{{ summary() }}</p>
        <div class="flex items-center gap-1">
          <button
            type="button"
            class="btn btn-secondary btn-sm"
            [disabled]="page() <= 1"
            (click)="pageChange.emit(page() - 1)"
            aria-label="Önceki sayfa"
          >
            <svg lucideChevronLeft></svg>
          </button>
          <span class="px-2 font-mono text-xs">{{ page() }} / {{ totalPages() }}</span>
          <button
            type="button"
            class="btn btn-secondary btn-sm"
            [disabled]="page() >= totalPages()"
            (click)="pageChange.emit(page() + 1)"
            aria-label="Sonraki sayfa"
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
  readonly pageChange = output<number>();

  protected readonly summary = computed(() => `Toplam ${formatNumber(this.totalCount())} kayıt`);
}
