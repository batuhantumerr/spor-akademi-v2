import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { LucideMegaphone } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1MeAnnouncementsGet } from '../../api/functions';
import { AnnouncementDto } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { PagedList } from '../../shared/data/paged-list';
import { AnnouncementItemComponent } from './announcement-item.component';

/** Compact "Duyurular" on the coach's home: the newest few, with a link to the full list. */
@Component({
  selector: 'app-coach-announcements',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, AnnouncementItemComponent, LucideMegaphone],
  template: `
    <section class="card p-4" aria-labelledby="coach-announcements-title">
      <div class="mb-2 flex items-center justify-between gap-2">
        <h2 id="coach-announcements-title" class="flex items-center gap-2 text-lg font-semibold">
          <svg lucideMegaphone class="size-5 text-primary" aria-hidden="true"></svg>
          {{ text.title }}
        </h2>
        @if (!list.error() && !list.loading() && !list.isEmpty()) {
          <a routerLink="/coach/announcements" class="btn btn-ghost btn-sm h-11">{{ text.all }}</a>
        }
      </div>
      <div [attr.aria-busy]="list.loading() || list.refreshing()">
        @if (list.error(); as message) {
          <div
            role="alert"
            class="flex flex-wrap items-center justify-between gap-2 text-sm text-destructive"
          >
            <span>{{ message }}</span>
            <button type="button" class="btn btn-secondary btn-sm h-11" (click)="list.load()">
              {{ common.retry }}
            </button>
          </div>
        } @else if (list.loading()) {
          <p role="status" class="text-sm text-muted-foreground">{{ common.loading }}</p>
        } @else if (list.isEmpty()) {
          <p class="text-sm text-muted-foreground">{{ text.emptyCoach }}</p>
        } @else {
          <ul class="divide-y divide-border/60">
            @for (a of list.items(); track a.id) {
              <li class="py-2">
                <app-announcement-item [announcement]="a" [showGroup]="true" [compact]="true" />
              </li>
            }
          </ul>
        }
      </div>
    </section>
  `,
})
export class CoachAnnouncementsComponent {
  private readonly api = inject(Api);

  protected readonly text = ui.announcements;
  protected readonly common = ui.common;
  /** The API returns the newest first. */
  protected readonly list = new PagedList<AnnouncementDto>((page) =>
    this.api.invoke(apiV1MeAnnouncementsGet, { page, pageSize: 3 }),
  );

  constructor() {
    void this.list.load();
    // A new announcement for one of the coach's groups, or a reconnect: reload in place (P3-08).
    inject(RealtimeService)
      .announcementsChanged()
      .pipe(takeUntilDestroyed())
      .subscribe(() => void this.list.load());
  }
}
