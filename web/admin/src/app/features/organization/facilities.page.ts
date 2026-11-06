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
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { ToastService } from '../../shared/ui/toast.service';
import { FacilityFormDialog } from './facility-form.dialog';

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
      <button type="button" class="btn btn-primary" (click)="openForm(null)">
        <svg lucidePlus></svg>{{ text.add }}
      </button>
    </app-page-header>

    @if (loading()) {
      <p class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
    } @else if (error()) {
      <div class="card flex items-center justify-between gap-3 p-6 text-sm text-destructive">
        <span>{{ error() }}</span>
        <button type="button" class="btn btn-secondary btn-sm" (click)="load()">
          {{ common.retry }}
        </button>
      </div>
    } @else if (facilities().length === 0) {
      <div class="card flex flex-col items-center gap-3 px-6 py-14 text-center">
        <div class="grid size-12 place-items-center rounded-2xl bg-primary/10 text-primary">
          <svg lucideMapPin class="size-6"></svg>
        </div>
        <p class="text-sm text-muted-foreground">{{ text.empty }}</p>
      </div>
    } @else {
      <div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
        @for (facility of facilities(); track facility.id) {
          <article
            class="card flex flex-col p-4 transition-shadow hover:shadow-md"
            [class.opacity-70]="facility.isArchived"
          >
            <div class="flex items-start justify-between gap-3">
              <div class="min-w-0">
                <h2 class="truncate text-base font-semibold">{{ facility.name }}</h2>
                <p class="mt-0.5 text-xs text-muted-foreground">{{ text.types[facility.type] }}</p>
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
              <svg lucideMapPin class="mt-0.5 size-4 shrink-0"></svg>
              <span
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
              <button type="button" class="btn btn-ghost btn-sm" (click)="openForm(facility)">
                <svg lucidePencil></svg>{{ actions.edit }}
              </button>
              @if (!facility.isArchived) {
                <button
                  type="button"
                  class="btn btn-ghost btn-sm btn-icon"
                  [attr.aria-label]="actions.archive"
                  [attr.title]="actions.archive"
                  (click)="archive(facility)"
                >
                  <svg lucideArchive></svg>
                </button>
              }
              <button
                type="button"
                class="btn btn-ghost btn-sm btn-icon hover:!bg-destructive/10 hover:!text-destructive"
                [attr.aria-label]="actions.delete"
                [attr.title]="actions.delete"
                (click)="remove(facility)"
              >
                <svg lucideTrash2></svg>
              </button>
            </div>
          </article>
        }
      </div>
      <div class="mt-4 card overflow-hidden">
        <app-pagination
          [page]="page()"
          [totalPages]="totalPages()"
          [totalCount]="totalCount()"
          (pageChange)="goTo($event)"
        />
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

  protected readonly facilities = signal<FacilityDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly showArchived = signal(false);
  protected readonly page = signal(1);
  protected readonly totalPages = signal(0);
  protected readonly totalCount = signal(0);

  constructor() {
    void this.load();
  }

  protected format(value: number | string): string {
    return formatNumber(Number(value));
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const result = await this.api.invoke(apiV1FacilitiesGet, {
        includeArchived: this.showArchived(),
        page: this.page(),
        pageSize: 30,
      });
      this.facilities.set(result.items);
      this.totalPages.set(Number(result.totalPages));
      this.totalCount.set(Number(result.totalCount));
    } catch (err) {
      this.error.set(problemMessage(toProblem(err)));
    } finally {
      this.loading.set(false);
    }
  }

  protected toggleArchived(value: boolean): void {
    this.showArchived.set(value);
    this.page.set(1);
    void this.load();
  }

  protected goTo(page: number): void {
    this.page.set(page);
    void this.load();
  }

  protected async openForm(facility: FacilityDto | null): Promise<void> {
    const ref = this.dialog.open<boolean, FacilityDto | null>(FacilityFormDialog, {
      data: facility,
      backdropClass: ['bg-slate-900/60'],
    });
    if (await firstValueFrom(ref.closed)) {
      this.toasts.success(this.text.saved);
      await this.load();
    }
  }

  protected async archive(facility: FacilityDto): Promise<void> {
    if (
      await this.confirmer.confirm({
        title: this.text.archiveTitle,
        message: this.text.archiveText,
        confirmLabel: this.actions.archive,
      })
    ) {
      await this.change(
        () => this.api.invoke(apiV1FacilitiesIdArchivePost, { id: facility.id }),
        this.text.archived,
      );
    }
  }

  protected async remove(facility: FacilityDto): Promise<void> {
    const ok = await this.confirmer.confirm({
      title: this.text.deleteTitle,
      message: this.text.deleteText,
      confirmLabel: this.actions.delete,
      destructive: true,
    });
    if (ok) {
      await this.change(
        () => this.api.invoke(apiV1FacilitiesIdDelete, { id: facility.id }),
        this.text.deleted,
      );
    }
  }

  private async change(action: () => Promise<unknown>, success: string): Promise<void> {
    try {
      await action();
      this.toasts.success(success);
      await this.load();
    } catch (err) {
      this.toasts.error(problemMessage(toProblem(err)));
    }
  }
}
