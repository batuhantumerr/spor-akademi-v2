import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideArrowLeft } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1MeAnnouncementsGet } from '../../api/functions';
import { AnnouncementDto } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { PagedList } from '../../shared/data/paged-list';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { AnnouncementItemComponent } from './announcement-item.component';

/** All announcements for the coach's groups, newest first. */
@Component({
  selector: 'app-coach-announcements-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, AnnouncementItemComponent, PaginationComponent, LucideArrowLeft],
  template: `
    <div class="mx-auto w-full max-w-xl">
      <a routerLink="/coach/today" class="btn btn-ghost mb-2 -ml-2 h-11">
        <svg lucideArrowLeft></svg>{{ text.back }}
      </a>
      <h1 class="mb-4 text-xl font-bold tracking-tight">{{ text.title }}</h1>
      @if (list.error(); as message) {
        <div role="alert" class="card flex flex-col gap-3 p-4 text-sm text-destructive">
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary h-12" (click)="list.load()">
            {{ common.retry }}
          </button>
        </div>
      } @else if (list.loading()) {
        <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else if (list.isEmpty()) {
        <p class="card p-6 text-base text-muted-foreground">{{ text.emptyCoach }}</p>
      } @else {
        <ul class="space-y-3" [attr.aria-busy]="list.refreshing()">
          @for (a of list.items(); track a.id) {
            <li class="card p-4">
              <app-announcement-item [announcement]="a" [showGroup]="true" [headingLevel]="2" />
            </li>
          }
        </ul>
        <div class="card mt-4 overflow-hidden">
          <app-pagination
            [page]="list.page()"
            [totalPages]="list.totalPages()"
            [totalCount]="list.totalCount()"
            [disabled]="list.refreshing()"
            (pageChange)="list.goTo($event)"
          />
        </div>
      }
    </div>
  `,
})
export class CoachAnnouncementsPage {
  private readonly api = inject(Api);

  protected readonly text = ui.announcements;
  protected readonly common = ui.common;
  protected readonly list = new PagedList<AnnouncementDto>((page) =>
    this.api.invoke(apiV1MeAnnouncementsGet, { page, pageSize: 20 }),
  );

  constructor() {
    void this.list.load();
  }
}
