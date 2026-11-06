import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { AnnouncementDto } from '../../api/models';
import { absoluteTime, relativeTime } from '../../core/format/relative-time';
import { interpolate, ui } from '../../core/i18n/tr';

/**
 * One announcement. The body is plain text shown with its line breaks (`whitespace-pre-line`) and
 * interpolated as text, never as HTML.
 */
@Component({
  selector: 'app-announcement-item',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let a = announcement();
    <article class="min-w-0" [attr.aria-labelledby]="'ann-' + a.id">
      <div class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <h3 [id]="'ann-' + a.id" class="min-w-0 text-base font-semibold break-words">
          {{ a.title }}
        </h3>
        <time
          class="shrink-0 text-xs text-muted-foreground"
          [attr.datetime]="a.createdAt"
          [attr.title]="absolute()"
          >{{ relative() }}</time
        >
      </div>
      @if (showGroup()) {
        <p class="text-xs text-muted-foreground">{{ groupLabel() }}</p>
      }
      <p
        class="mt-1 text-sm break-words whitespace-pre-line text-foreground"
        [class.line-clamp-2]="compact()"
        [textContent]="a.body"
      ></p>
    </article>
  `,
})
export class AnnouncementItemComponent {
  readonly announcement = input.required<AnnouncementDto>();
  readonly showGroup = input(false);
  /** Two lines of the body only (the coach's home list). */
  readonly compact = input(false);
  /** Injected for tests; defaults to the current time. */
  readonly now = input<Date | undefined>(undefined);

  protected readonly relative = computed(() =>
    relativeTime(this.announcement().createdAt, this.now() ?? new Date()),
  );
  protected readonly absolute = computed(() => absoluteTime(this.announcement().createdAt));
  protected readonly groupLabel = computed(() =>
    interpolate(ui.announcements.groupLabel, { name: this.announcement().groupName }),
  );
}
