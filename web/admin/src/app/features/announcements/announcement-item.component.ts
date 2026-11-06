import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';

import { AnnouncementDto } from '../../api/models';
import { absoluteTime, relativeTime } from '../../core/format/relative-time';
import { interpolate, ui } from '../../core/i18n/tr';
import { SchoolClock } from '../../core/time/school-clock';

/**
 * One announcement. The body is plain text shown with its line breaks (`whitespace-pre-line`) and
 * set as text, never as HTML. Dates are relative in the school's time zone.
 */
@Component({
  selector: 'app-announcement-item',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let a = announcement();
    <article class="min-w-0" [attr.aria-labelledby]="'ann-' + a.id">
      <div class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        @if (headingLevel() === 2) {
          <h2 [id]="'ann-' + a.id" class="min-w-0 text-base font-semibold break-words">
            {{ a.title }}
          </h2>
        } @else {
          <h3 [id]="'ann-' + a.id" class="min-w-0 text-base font-semibold break-words">
            {{ a.title }}
          </h3>
        }
        <time
          class="shrink-0 text-xs text-muted-foreground"
          [attr.datetime]="a.createdAt"
          [attr.title]="absolute()"
          ><span aria-hidden="true">{{ relative() }}</span
          ><span class="sr-only">{{ relative() }}, {{ postedAt() }}</span></time
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
  private readonly clock = inject(SchoolClock);

  readonly announcement = input.required<AnnouncementDto>();
  readonly showGroup = input(false);
  /** Two lines of the body only (the coach's home list). */
  readonly compact = input(false);
  /** 2 directly under a page h1; 3 inside a section with its own h2. */
  readonly headingLevel = input<2 | 3>(3);
  /** Injected for tests; defaults to the current time. */
  readonly now = input<Date | undefined>(undefined);

  protected readonly relative = computed(() => {
    // Re-evaluated when the school's day turns over, so "bugün" items become "dün".
    this.clock.today();
    return relativeTime(
      this.announcement().createdAt,
      this.now() ?? new Date(),
      this.clock.timeZone(),
    );
  });
  protected readonly absolute = computed(() =>
    absoluteTime(this.announcement().createdAt, this.clock.timeZone()),
  );
  /** The exact time, readable without hovering (screen readers, touch). */
  protected readonly postedAt = computed(() =>
    interpolate(ui.announcements.postedAt, { date: this.absolute() }),
  );
  protected readonly groupLabel = computed(() =>
    interpolate(ui.announcements.groupLabel, { name: this.announcement().groupName }),
  );
}
