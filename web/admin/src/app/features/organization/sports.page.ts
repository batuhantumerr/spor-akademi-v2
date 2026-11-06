import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  LucideArchive,
  LucideCheck,
  LucideDumbbell,
  LucidePencil,
  LucidePlus,
  LucideTrash2,
  LucideX,
} from '@lucide/angular';

import { Api } from '../../api/api';
import {
  apiV1SportsGet,
  apiV1SportsIdArchivePost,
  apiV1SportsIdDelete,
  apiV1SportsIdPut,
  apiV1SportsPost,
} from '../../api/functions';
import { SportDto } from '../../api/models';
import { fieldErrors, problemMessage, toProblem } from '../../core/api/problem';
import { ui } from '../../core/i18n/tr';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { ToastService } from '../../shared/ui/toast.service';

/** US-06: sports of the school — add, rename, archive, delete. */
@Component({
  selector: 'app-sports-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    PageHeaderComponent,
    PaginationComponent,
    LucideArchive,
    LucideCheck,
    LucideDumbbell,
    LucidePencil,
    LucidePlus,
    LucideTrash2,
    LucideX,
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
    </app-page-header>

    <form
      class="card mb-4 flex flex-col gap-3 p-4 sm:flex-row sm:items-start"
      (ngSubmit)="create()"
    >
      <div class="flex-1">
        <label class="sr-only" for="new-sport">{{ text.name }}</label>
        <input
          id="new-sport"
          name="name"
          class="input"
          [placeholder]="text.namePlaceholder"
          [(ngModel)]="newName"
          maxlength="100"
          [attr.aria-invalid]="createError() !== null"
        />
        @if (createError()) {
          <p class="field-error">{{ createError() }}</p>
        }
      </div>
      <button type="submit" class="btn btn-primary" [disabled]="busy() || !newName.trim()">
        <svg lucidePlus></svg>{{ text.add }}
      </button>
    </form>

    <div class="table-shell">
      @if (loading()) {
        <p class="p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else if (error()) {
        <div class="flex items-center justify-between gap-3 p-6 text-sm text-destructive">
          <span>{{ error() }}</span>
          <button type="button" class="btn btn-secondary btn-sm" (click)="load()">
            {{ common.retry }}
          </button>
        </div>
      } @else if (sports().length === 0) {
        <div class="flex flex-col items-center gap-3 px-6 py-14 text-center">
          <div class="grid size-12 place-items-center rounded-2xl bg-primary/10 text-primary">
            <svg lucideDumbbell class="size-6"></svg>
          </div>
          <p class="text-sm text-muted-foreground">{{ text.empty }}</p>
        </div>
      } @else {
        <table class="data-table">
          <thead>
            <tr>
              <th scope="col">{{ table.name }}</th>
              <th scope="col">{{ table.status }}</th>
              <th scope="col" class="text-right">{{ table.actions }}</th>
            </tr>
          </thead>
          <tbody>
            @for (sport of sports(); track sport.id) {
              <tr>
                <td>
                  @if (editingId() === sport.id) {
                    <form class="flex items-center gap-2" (ngSubmit)="saveRename(sport)">
                      <label class="sr-only" [for]="'rename-' + sport.id">{{ text.name }}</label>
                      <input
                        [id]="'rename-' + sport.id"
                        name="rename"
                        class="input h-9"
                        [(ngModel)]="editName"
                        maxlength="100"
                      />
                      <button
                        type="submit"
                        class="btn btn-primary btn-sm btn-icon"
                        [attr.aria-label]="actions.save"
                        [disabled]="busy()"
                      >
                        <svg lucideCheck></svg>
                      </button>
                      <button
                        type="button"
                        class="btn btn-ghost btn-sm btn-icon"
                        [attr.aria-label]="actions.cancel"
                        (click)="editingId.set(null)"
                      >
                        <svg lucideX></svg>
                      </button>
                    </form>
                  } @else {
                    <span class="font-semibold">{{ sport.name }}</span>
                  }
                </td>
                <td>
                  <span
                    class="badge"
                    [class.badge-success]="!sport.isArchived"
                    [class.badge-muted]="sport.isArchived"
                  >
                    {{ sport.isArchived ? table.archived : table.active }}
                  </span>
                </td>
                <td>
                  <div class="flex justify-end gap-1">
                    <button
                      type="button"
                      class="btn btn-ghost btn-sm btn-icon"
                      [attr.aria-label]="actions.rename"
                      [attr.title]="actions.rename"
                      (click)="startRename(sport)"
                    >
                      <svg lucidePencil></svg>
                    </button>
                    @if (!sport.isArchived) {
                      <button
                        type="button"
                        class="btn btn-ghost btn-sm btn-icon"
                        [attr.aria-label]="actions.archive"
                        [attr.title]="actions.archive"
                        (click)="archive(sport)"
                      >
                        <svg lucideArchive></svg>
                      </button>
                    }
                    <button
                      type="button"
                      class="btn btn-ghost btn-sm btn-icon hover:!bg-destructive/10 hover:!text-destructive"
                      [attr.aria-label]="actions.delete"
                      [attr.title]="actions.delete"
                      (click)="remove(sport)"
                    >
                      <svg lucideTrash2></svg>
                    </button>
                  </div>
                </td>
              </tr>
            }
          </tbody>
        </table>
        <app-pagination
          [page]="page()"
          [totalPages]="totalPages()"
          [totalCount]="totalCount()"
          (pageChange)="goTo($event)"
        />
      }
    </div>
  `,
})
export class SportsPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(ToastService);
  private readonly confirmer = inject(ConfirmService);

  protected readonly text = ui.sports;
  protected readonly table = ui.table;
  protected readonly actions = ui.actions;
  protected readonly common = ui.common;

  protected readonly sports = signal<SportDto[]>([]);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly createError = signal<string | null>(null);
  protected readonly showArchived = signal(false);
  protected readonly page = signal(1);
  protected readonly totalPages = signal(0);
  protected readonly totalCount = signal(0);
  protected readonly editingId = signal<string | null>(null);
  protected newName = '';
  protected editName = '';

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const result = await this.api.invoke(apiV1SportsGet, {
        includeArchived: this.showArchived(),
        page: this.page(),
        pageSize: 50,
      });
      this.sports.set(result.items);
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

  protected async create(): Promise<void> {
    this.createError.set(null);
    await this.run(
      async () => {
        await this.api.invoke(apiV1SportsPost, { body: { name: this.newName } });
        this.newName = '';
        this.toasts.success(this.text.created);
      },
      (message, fields) => this.createError.set(fields['name'] ?? message),
    );
  }

  protected startRename(sport: SportDto): void {
    this.editName = sport.name;
    this.editingId.set(sport.id);
  }

  protected async saveRename(sport: SportDto): Promise<void> {
    await this.run(async () => {
      await this.api.invoke(apiV1SportsIdPut, {
        id: sport.id,
        body: { name: this.editName, version: Number(sport.version) },
      });
      this.editingId.set(null);
      this.toasts.success(this.text.renamed);
    });
  }

  protected async archive(sport: SportDto): Promise<void> {
    const ok = await this.confirmer.confirm({
      title: this.text.archiveTitle,
      message: this.text.archiveText,
      confirmLabel: this.actions.archive,
    });
    if (ok) {
      await this.run(async () => {
        await this.api.invoke(apiV1SportsIdArchivePost, { id: sport.id });
        this.toasts.success(this.text.archived);
      });
    }
  }

  protected async remove(sport: SportDto): Promise<void> {
    const ok = await this.confirmer.confirm({
      title: this.text.deleteTitle,
      message: this.text.deleteText,
      confirmLabel: this.actions.delete,
      destructive: true,
    });
    if (ok) {
      await this.run(async () => {
        await this.api.invoke(apiV1SportsIdDelete, { id: sport.id });
        this.toasts.success(this.text.deleted);
      });
    }
  }

  /** Runs a change, reloads on success, and reports the Turkish problem message on failure. */
  private async run(
    action: () => Promise<void>,
    onError?: (message: string, fields: Record<string, string>) => void,
  ): Promise<void> {
    this.busy.set(true);
    try {
      await action();
      await this.load();
    } catch (err) {
      const problem = toProblem(err);
      const message = problemMessage(problem);
      if (onError) {
        onError(message, fieldErrors(problem));
      } else {
        this.toasts.error(message);
      }
    } finally {
      this.busy.set(false);
    }
  }
}
