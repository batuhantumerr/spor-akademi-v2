import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { LucideCalendarDays, LucideMapPin, LucideUsers } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1MeGroupsGet } from '../../api/functions';
import { GroupDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { ui } from '../../core/i18n/tr';
import { summarizeSchedule } from '../../core/schedule/schedule';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';

/** US-12: the coach's own groups as phone-friendly cards. */
@Component({
  selector: 'app-coach-groups-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageHeaderComponent, LucideCalendarDays, LucideMapPin, LucideUsers],
  template: `
    <div class="mx-auto w-full max-w-xl">
      <app-page-header [title]="text.title" [description]="text.description" />
      @if (error(); as message) {
        <div role="alert" class="card flex flex-col gap-3 p-4 text-sm text-destructive">
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary h-12" (click)="load()">
            {{ common.retry }}
          </button>
        </div>
      } @else if (groups() === null) {
        <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else if (groups()!.length === 0) {
        <div class="card flex flex-col items-center gap-3 px-6 py-12 text-center">
          <div class="grid size-12 place-items-center rounded-2xl bg-primary/10 text-primary">
            <svg lucideUsers class="size-6"></svg>
          </div>
          <p class="text-base text-muted-foreground">{{ text.empty }}</p>
        </div>
      } @else {
        <ul class="space-y-3">
          @for (group of groups(); track group.id) {
            <li class="card p-4">
              <h2 class="text-lg font-semibold">{{ group.name }}</h2>
              <p class="text-sm text-muted-foreground">
                {{ group.sport.name }} ·
                <span class="font-mono"
                  >{{ group.birthYearRange.from }}–{{ group.birthYearRange.to }}</span
                >
              </p>
              <p class="mt-3 flex items-start gap-1.5 text-base">
                <svg
                  lucideCalendarDays
                  class="mt-1 size-4 shrink-0 text-primary"
                  aria-hidden="true"
                ></svg>
                <span>
                  <span class="sr-only">{{ groupsText.schedule }}: </span>{{ schedule(group) }}
                </span>
              </p>
              <p class="mt-1 flex items-center gap-1.5 text-base text-muted-foreground">
                <svg lucideMapPin class="size-4 shrink-0" aria-hidden="true"></svg>
                <span class="sr-only">{{ groupsText.facility }}: </span>{{ group.facility.name }}
              </p>
            </li>
          }
        </ul>
      }
    </div>
  `,
})
export class CoachGroupsPage {
  private readonly api = inject(Api);

  protected readonly text = ui.coachGroups;
  protected readonly groupsText = ui.groups;
  protected readonly common = ui.common;
  protected readonly groups = signal<GroupDto[] | null>(null);
  protected readonly error = signal<string | null>(null);

  constructor() {
    void this.load();
  }

  protected schedule(group: GroupDto): string {
    return summarizeSchedule(group.slots);
  }

  async load(): Promise<void> {
    this.error.set(null);
    try {
      this.groups.set(await this.api.invoke(apiV1MeGroupsGet));
    } catch (err) {
      this.error.set(problemMessage(toProblem(err)));
    }
  }
}
