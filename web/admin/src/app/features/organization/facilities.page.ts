import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  LucideArchive,
  LucideMapPin,
  LucidePencil,
  LucidePlus,
  LucideTrash2,
} from '@lucide/angular';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../api/api';
import {
  apiV1FacilitiesGet,
  apiV1FacilitiesIdArchivePost,
  apiV1FacilitiesIdDelete,
} from '../../api/functions';
import { FacilityDto } from '../../api/models';
import { formatNumber } from '../../core/format/format';
import { problemMessage, toProblem } from '../../core/api/problem';
import { ui } from '../../core/i18n/tr';
import { PagedList } from '../../shared/data/paged-list';
import { ConfirmOptions, ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { ToastService } from '../../shared/ui/toast.service';
import { FACILITY_FORM_TITLE_ID, FacilityFormDialog } from './facility-form.dialog';

/** US-07: fields, pools and courts of the school. */
@Component({
  selector: 'app-facilities-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    PageHeaderComponent,
    PaginationComponent,
    LucideArchive,
    LucideMapPin,
    LucidePencil,
    LucidePlus,
    LucideTrash2,
  ],
  template: `
    <app-page-header [title]="text.title" [description]="text.description">
      <label class="flex cursor-pointer items-center gap-2 text-sm text-muted-foreground">
        <input
          type="checkbox"
          class="size-4 accent-[var(--primary)]"
          [ngModel]="showArchived()"
          (ngModelChange)="toggleArchived($event)"
        />
        {{ text.showArchived }}
      </label>
      <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="openForm(null)">
        <svg lucidePlus></svg>{{ text.add }}
      </button>
    </app-page-header>

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
          <svg lucideMapPin class="size-6"></svg>
        </div>
        <p class="text-sm text-muted-foreground">
          {{ showArchived() ? text.empty : text.emptyActive }}
        </p>
      </div>
    } @else if (list.items().length > 0) {
      <div
        class="transition-opacity"
        [class.opacity-60]="list.refreshing()"
        [attr.aria-busy]="list.refreshing()"
      >
        <div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
          @for (facility of list.items(); track facility.id) {
            <article
              class="card flex min-w-0 flex-col p-4 transition-shadow hover:shadow-md"
              [class.opacity-70]="facility.isArchived"
            >
              <div class="flex items-start justify-between gap-3">
                <div class="min-w-0">
                  <h2 class="truncate text-base font-semibold">{{ facility.name }}</h2>
                  <p class="mt-0.5 text-xs text-muted-foreground">
                    {{ text.types[facility.type] }}
                  </p>
                </div>
                <span
                  class="badge"
                  [class.badge-success]="!facility.isArchived"
                  [class.badge-muted]="facility.isArchived"
                >
                  {{ facility.isArchived ? table.archived : table.active }}
                </span>
              </div>
              <p class="mt-3 flex items-start gap-1.5 text-sm text-muted-foreground">
                <svg lucideMapPin class="mt-0.5 size-4 shrink-0" aria-hidden="true"></svg>
                <span class="min-w-0 break-words"
                  >{{ facility.address.line }}, {{ facility.address.district }} /
                  {{ facility.address.city }}</span
                >
              </p>
              @if (facility.capacity !== null) {
                <p class="mt-2 font-mono text-xs text-muted-foreground">
                  {{ text.capacityShort }}: {{ format(facility.capacity) }}
                </p>
              }
              <div class="mt-4 flex justify-end gap-1 border-t border-border/60 pt-3">
                <button
                  type="button"
                  class="btn btn-ghost btn-sm"
                  [attr.aria-label]="actions.edit + ': ' + facility.name"
                  [disabled]="busy()"
                  (click)="openForm(facility)"
                >
                  <svg lucidePencil></svg>{{ actions.edit }}
                </button>
                @if (!facility.isArchived) {
                  <button
                    type="button"
                    class="btn btn-ghost btn-sm btn-icon"
                    [attr.aria-label]="actions.archive + ': ' + facility.name"
                    [attr.title]="actions.archive"
                    [disabled]="busy()"
                    (click)="archive(facility)"
                  >
                    <svg lucideArchive></svg>
                  </button>
                }
                <button
                  type="button"
                  class="btn btn-ghost btn-sm btn-icon hover:!bg-destructive/10 hover:!text-destructive"
                  [attr.aria-label]="actions.delete + ': ' + facility.name"
                  [attr.title]="actions.delete"
                  [disabled]="busy()"
                  (click)="remove(facility)"
                >
                  <svg lucideTrash2></svg>
                </button>
              </div>
            </article>
          }
        </div>
        @if (list.totalPages() > 1) {
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
    }
  `,
})
export class FacilitiesPage {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toasts = inject(ToastService);
  private readonly confirmer = inject(ConfirmService);

  protected readonly text = ui.facilities;
  protected readonly table = ui.table;
  protected readonly actions = ui.actions;
  protected readonly common = ui.common;

  protected readonly showArchived = signal(false);
  protected readonly busy = signal(false);
  protected readonly list = new PagedList<FacilityDto>((page) =>
    this.api.invoke(apiV1FacilitiesGet, {
      includeArchived: this.showArchived(),
      page,
      pageSize: 30,
    }),
  );

  constructor() {
    void this.list.load();
  }

  protected format(value: number | string): string {
    return formatNumber(Number(value));
  }

  protected toggleArchived(value: boolean): void {
    this.showArchived.set(value);
    void this.list.reset();
  }

  protected async openForm(facility: FacilityDto | null): Promise<void> {
    await this.exclusive(async () => {
      const ref = this.dialog.open<boolean, FacilityDto | null>(FacilityFormDialog, {
        data: facility,
        ariaLabelledBy: FACILITY_FORM_TITLE_ID,
        backdropClass: ['bg-slate-900/60'],
      });
      if (await firstValueFrom(ref.closed)) {
        this.toasts.success(this.text.saved);
      }
      // Reload either way: a version conflict inside the dialog may have left the list stale.
      await this.list.load();
    });
  }

  protected async archive(facility: FacilityDto): Promise<void> {
    await this.change(
      {
        title: this.text.archiveTitle,
        message: this.text.archiveText,
        confirmLabel: this.actions.archive,
      },
      () => this.api.invoke(apiV1FacilitiesIdArchivePost, { id: facility.id }),
      this.text.archived,
    );
  }

  protected async remove(facility: FacilityDto): Promise<void> {
    await this.change(
      {
        title: this.text.deleteTitle,
        message: this.text.deleteText,
        confirmLabel: this.actions.delete,
        destructive: true,
      },
      () => this.api.invoke(apiV1FacilitiesIdDelete, { id: facility.id }),
      this.text.deleted,
    );
  }

  /** Confirm, run, toast and reload; one change at a time. */
  private async change(
    confirm: ConfirmOptions,
    action: () => Promise<unknown>,
    success: string,
  ): Promise<void> {
    await this.exclusive(async () => {
      if (!(await this.confirmer.confirm(confirm))) {
        return;
      }
      try {
        await action();
        this.toasts.success(success);
      } catch (err) {
        this.toasts.error(problemMessage(toProblem(err)));
      }
      await this.list.load();
    });
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
