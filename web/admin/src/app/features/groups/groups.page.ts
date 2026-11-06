import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideArchive, LucideCalendarDays, LucidePencil, LucidePlus } from '@lucide/angular';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../api/api';
import { apiV1GroupsGet, apiV1GroupsIdArchivePost } from '../../api/functions';
import { GroupDto, GroupStatus } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { ui } from '../../core/i18n/tr';
import { summarizeSchedule } from '../../core/schedule/schedule';
import { PagedList } from '../../shared/data/paged-list';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { ToastService } from '../../shared/ui/toast.service';
import { GROUP_FORM_TITLE_ID, GroupFormData, GroupFormDialog } from './group-form.dialog';
import { GroupLookups, loadGroupLookups } from './group-lookups';

type StatusFilter = GroupStatus | '';

/** US-08: the school's groups with sport, coach and status filters. */
@Component({
  selector: 'app-groups-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    PageHeaderComponent,
    PaginationComponent,
    LucideArchive,
    LucideCalendarDays,
    LucidePencil,
    LucidePlus,
  ],
  template: `
    <app-page-header [title]="text.title" [description]="text.description">
      <button
        type="button"
        class="btn btn-primary"
        [disabled]="busy() || !lookups()"
        (click)="openForm(null)"
      >
        <svg lucidePlus></svg>{{ text.add }}
      </button>
    </app-page-header>

    <fieldset class="card mb-4 grid gap-3 p-4 sm:grid-cols-3">
      <legend class="sr-only">{{ text.filters }}</legend>
      <div class="min-w-0">
        <label class="field-label" for="filter-sport">{{ text.sport }}</label>
        <select
          id="filter-sport"
          class="input"
          [value]="sportId()"
          (change)="setFilter('sport', $any($event.target).value)"
        >
          <option value="">{{ text.allSports }}</option>
          @for (sport of lookups()?.sports ?? []; track sport.id) {
            <option [value]="sport.id">{{ sport.name }}</option>
          }
        </select>
      </div>
      <div class="min-w-0">
        <label class="field-label" for="filter-coach">{{ text.coach }}</label>
        <select
          id="filter-coach"
          class="input"
          [value]="coachId()"
          (change)="setFilter('coach', $any($event.target).value)"
        >
          <option value="">{{ text.allCoaches }}</option>
          @for (coach of lookups()?.coaches ?? []; track coach.id) {
            <option [value]="coach.id">{{ coach.name }}</option>
          }
        </select>
      </div>
      <div class="min-w-0">
        <label class="field-label" for="filter-status">{{ text.status }}</label>
        <select
          id="filter-status"
          class="input"
          [value]="status()"
          (change)="setFilter('status', $any($event.target).value)"
        >
          <option value="Active">{{ text.statuses['Active'] }}</option>
          <option value="Archived">{{ text.statuses['Archived'] }}</option>
          <option value="">{{ text.allStatuses }}</option>
        </select>
      </div>
    </fieldset>

    @if (lookupError()) {
      <p role="alert" class="card mb-4 p-4 text-sm text-destructive">{{ lookupError() }}</p>
    }

    @if (list.error(); as message) {
      <div
        role="alert"
        class="card mb-4 flex items-center justify-between gap-3 p-4 text-sm text-destructive"
      >
        <span>{{ message }}</span>
        <button type="button" class="btn btn-secondary btn-sm" (click)="list.load()">
          {{ common.retry }}
        </button>
      </div>
    }

    @if (list.loading()) {
      <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
    } @else if (list.isEmpty()) {
      <div class="card flex flex-col items-center gap-3 px-6 py-14 text-center">
        <div class="grid size-12 place-items-center rounded-2xl bg-primary/10 text-primary">
          <svg lucideCalendarDays class="size-6"></svg>
        </div>
        <p class="text-sm text-muted-foreground">
          {{ filtered() ? text.emptyFiltered : text.empty }}
        </p>
      </div>
    } @else if (list.items().length > 0) {
      <div
        class="table-shell transition-opacity"
        tabindex="0"
        role="region"
        aria-labelledby="groups-caption"
        [class.opacity-60]="list.refreshing()"
        [attr.aria-busy]="list.refreshing()"
      >
        <table class="data-table min-w-[56rem]">
          <caption id="groups-caption" class="sr-only">
            {{
              text.caption
            }}
          </caption>
          <thead>
            <tr>
              <th scope="col">{{ text.name }}</th>
              <th scope="col">{{ text.birthYears }}</th>
              <th scope="col">{{ text.capacity }}</th>
              <th scope="col">{{ text.coach }}</th>
              <th scope="col">{{ text.facility }}</th>
              <th scope="col">{{ text.schedule }}</th>
              <th scope="col">{{ text.status }}</th>
              <th scope="col" class="text-right">{{ table.actions }}</th>
            </tr>
          </thead>
          <tbody>
            @for (group of list.items(); track group.id) {
              <tr>
                <th scope="row">
                  <a
                    class="text-primary underline-offset-4 hover:underline"
                    [routerLink]="['/groups', group.id]"
                    >{{ group.name }}</a
                  >
                  <span class="block text-xs font-normal text-muted-foreground">{{
                    group.sport.name
                  }}</span>
                </th>
                <td class="font-mono text-xs whitespace-nowrap">
                  {{ group.birthYearRange.from }}–{{ group.birthYearRange.to }}
                </td>
                <td class="font-mono text-xs">{{ group.capacity }}</td>
                <td>
                  @if (group.coach?.isActive) {
                    {{ group.coach!.firstName }} {{ group.coach!.lastName }}
                  } @else {
                    <span class="badge badge-warning">{{ text.noCoach }}</span>
                  }
                </td>
                <td>{{ group.facility.name }}</td>
                <td class="text-xs">{{ schedule(group) }}</td>
                <td>
                  <span
                    class="badge"
                    [class.badge-success]="group.status === 'Active'"
                    [class.badge-muted]="group.status !== 'Active'"
                  >
                    {{ text.statuses[group.status] }}
                  </span>
                </td>
                <td>
                  <div class="flex justify-end gap-1">
                    @if (group.status === 'Active') {
                      <button
                        type="button"
                        class="btn btn-ghost btn-sm btn-icon"
                        [attr.aria-label]="actions.edit + ': ' + group.name"
                        [attr.title]="actions.edit"
                        [disabled]="busy() || !lookups()"
                        (click)="openForm(group)"
                      >
                        <svg lucidePencil></svg>
                      </button>
                      <button
                        type="button"
                        class="btn btn-ghost btn-sm btn-icon"
                        [attr.aria-label]="actions.archive + ': ' + group.name"
                        [attr.title]="actions.archive"
                        [disabled]="busy()"
                        (click)="archive(group)"
                      >
                        <svg lucideArchive></svg>
                      </button>
                    }
                  </div>
                </td>
              </tr>
            }
          </tbody>
        </table>
        <app-pagination
          [page]="list.page()"
          [totalPages]="list.totalPages()"
          [totalCount]="list.totalCount()"
          [disabled]="list.refreshing()"
          (pageChange)="list.goTo($event)"
        />
      </div>
    }
  `,
})
export class GroupsPage {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toasts = inject(ToastService);
  private readonly confirmer = inject(ConfirmService);

  protected readonly text = ui.groups;
  protected readonly table = ui.table;
  protected readonly actions = ui.actions;
  protected readonly common = ui.common;

  protected readonly sportId = signal('');
  protected readonly coachId = signal('');
  protected readonly status = signal<StatusFilter>('Active');
  protected readonly filtered = computed(
    () => this.sportId() !== '' || this.coachId() !== '' || this.status() !== '',
  );
  protected readonly busy = signal(false);
  protected readonly lookups = signal<GroupLookups | null>(null);
  protected readonly lookupError = signal<string | null>(null);
  protected readonly list = new PagedList<GroupDto>((page) =>
    this.api.invoke(apiV1GroupsGet, {
      sportId: this.sportId() || undefined,
      coachId: this.coachId() || undefined,
      status: this.status() || undefined,
      page,
      pageSize: 25,
    }),
  );

  constructor() {
    void this.list.load();
    void this.loadLookups();
  }

  protected schedule(group: GroupDto): string {
    return summarizeSchedule(group.slots);
  }

  protected setFilter(filter: 'sport' | 'coach' | 'status', value: string): void {
    if (filter === 'sport') {
      this.sportId.set(value);
    } else if (filter === 'coach') {
      this.coachId.set(value);
    } else {
      this.status.set(value as StatusFilter);
    }
    void this.list.reset();
  }

  protected async openForm(group: GroupDto | null): Promise<void> {
    const lookups = this.lookups();
    if (!lookups) {
      return;
    }
    await this.exclusive(async () => {
      const ref = this.dialog.open<boolean, GroupFormData>(GroupFormDialog, {
        data: { group, lookups },
        ariaLabelledBy: GROUP_FORM_TITLE_ID,
        backdropClass: ['bg-slate-900/60'],
      });
      if (await firstValueFrom(ref.closed)) {
        this.toasts.success(this.text.saved);
      }
      // Reload either way: a version conflict inside the dialog may have left the list stale.
      await this.list.load();
    });
  }

  protected async archive(group: GroupDto): Promise<void> {
    await this.exclusive(async () => {
      const ok = await this.confirmer.confirm({
        title: this.text.archiveTitle,
        message: this.text.archiveText,
        confirmLabel: this.actions.archive,
      });
      if (!ok) {
        return;
      }
      try {
        await this.api.invoke(apiV1GroupsIdArchivePost, { id: group.id });
        this.toasts.success(this.text.archived);
      } catch (err) {
        this.toasts.error(problemMessage(toProblem(err)));
      }
      await this.list.load();
    });
  }

  private async loadLookups(): Promise<void> {
    try {
      this.lookups.set(await loadGroupLookups(this.api));
    } catch {
      this.lookupError.set(this.text.lookupsFailed);
    }
  }

  /** Re-entry guard: buttons are disabled while busy, and a second call is ignored. */
  private async exclusive(work: () => Promise<void>): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      await work();
    } finally {
      this.busy.set(false);
    }
  }
}
