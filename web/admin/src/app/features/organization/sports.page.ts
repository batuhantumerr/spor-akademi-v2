import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  signal,
} from '@angular/core';
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
import { ApiProblem, fieldErrors, problemMessage, toProblem } from '../../core/api/problem';
import { errorMessages, ui } from '../../core/i18n/tr';
import { PagedList } from '../../shared/data/paged-list';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { ToastService } from '../../shared/ui/toast.service';

const NAME_MAX = 100;

/** Client check for a sport name; null when valid. */
function nameError(value: string): string | null {
  const name = value.trim();
  if (!name) {
    return errorMessages['Field.Required'];
  }
  return name.length > NAME_MAX ? errorMessages['Field.TooLong'] : null;
}

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
      novalidate
    >
      <div class="min-w-0 flex-1">
        <label class="sr-only" for="new-sport">{{ text.name }}</label>
        <input
          id="new-sport"
          name="name"
          class="input"
          [placeholder]="text.namePlaceholder"
          [ngModel]="newName()"
          (ngModelChange)="newName.set($event); createError.set(null)"
          [attr.maxlength]="nameMax"
          [attr.aria-invalid]="createError() !== null"
          [attr.aria-describedby]="createError() ? 'new-sport-error' : null"
        />
        @if (createError()) {
          <p id="new-sport-error" class="field-error">{{ createError() }}</p>
        }
      </div>
      <button type="submit" class="btn btn-primary" [disabled]="busy() || !newName().trim()">
        <svg lucidePlus></svg>{{ text.add }}
      </button>
    </form>

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
          <svg lucideDumbbell class="size-6"></svg>
        </div>
        <p class="text-sm text-muted-foreground">
          {{ showArchived() ? text.empty : text.emptyActive }}
        </p>
      </div>
    } @else if (list.items().length > 0) {
      <div
        class="table-shell transition-opacity"
        [class.opacity-60]="list.refreshing()"
        [attr.aria-busy]="list.refreshing()"
      >
        <table class="data-table">
          <thead>
            <tr>
              <th scope="col">{{ table.name }}</th>
              <th scope="col">{{ table.status }}</th>
              <th scope="col" class="text-right">{{ table.actions }}</th>
            </tr>
          </thead>
          <tbody>
            @for (sport of list.items(); track sport.id) {
              <tr>
                <td>
                  @if (editingId() === sport.id) {
                    <form class="flex items-start gap-2" (ngSubmit)="saveRename(sport)" novalidate>
                      <div class="min-w-0 flex-1">
                        <label class="sr-only" [for]="'rename-' + sport.id">{{ text.name }}</label>
                        <input
                          [id]="'rename-' + sport.id"
                          name="rename"
                          class="input h-9 min-w-0"
                          [ngModel]="editName()"
                          (ngModelChange)="editName.set($event); renameError.set(null)"
                          (keydown.escape)="cancelRename()"
                          [attr.maxlength]="nameMax"
                          [attr.aria-invalid]="renameError() !== null"
                          [attr.aria-describedby]="renameError() ? 'rename-error' : null"
                        />
                        @if (renameError()) {
                          <p id="rename-error" class="field-error">{{ renameError() }}</p>
                        }
                      </div>
                      <button
                        type="submit"
                        class="btn btn-primary btn-sm btn-icon shrink-0"
                        [attr.aria-label]="actions.save"
                        [disabled]="busy()"
                      >
                        <svg lucideCheck></svg>
                      </button>
                      <button
                        type="button"
                        class="btn btn-ghost btn-sm btn-icon shrink-0"
                        [attr.aria-label]="actions.cancel"
                        (click)="cancelRename()"
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
                      [id]="'rename-btn-' + sport.id"
                      [attr.aria-label]="actions.rename + ': ' + sport.name"
                      [attr.title]="actions.rename"
                      [disabled]="busy()"
                      (click)="startRename(sport)"
                    >
                      <svg lucidePencil></svg>
                    </button>
                    @if (!sport.isArchived) {
                      <button
                        type="button"
                        class="btn btn-ghost btn-sm btn-icon"
                        [attr.aria-label]="actions.archive + ': ' + sport.name"
                        [attr.title]="actions.archive"
                        [disabled]="busy()"
                        (click)="archive(sport)"
                      >
                        <svg lucideArchive></svg>
                      </button>
                    }
                    <button
                      type="button"
                      class="btn btn-ghost btn-sm btn-icon hover:!bg-destructive/10 hover:!text-destructive"
                      [attr.aria-label]="actions.delete + ': ' + sport.name"
                      [attr.title]="actions.delete"
                      [disabled]="busy()"
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
export class SportsPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(ToastService);
  private readonly confirmer = inject(ConfirmService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly text = ui.sports;
  protected readonly table = ui.table;
  protected readonly actions = ui.actions;
  protected readonly common = ui.common;
  protected readonly nameMax = NAME_MAX;

  protected readonly showArchived = signal(false);
  protected readonly list = new PagedList<SportDto>((page) =>
    this.api.invoke(apiV1SportsGet, {
      includeArchived: this.showArchived(),
      page,
      pageSize: 50,
    }),
  );
  protected readonly busy = signal(false);
  protected readonly createError = signal<string | null>(null);
  protected readonly editingId = signal<string | null>(null);
  protected readonly newName = signal('');
  protected readonly editName = signal('');
  protected readonly renameError = signal<string | null>(null);

  constructor() {
    void this.list.load();
  }

  protected toggleArchived(value: boolean): void {
    this.showArchived.set(value);
    void this.list.reset();
  }

  protected async create(): Promise<void> {
    const invalid = nameError(this.newName());
    if (invalid) {
      this.createError.set(invalid);
      return;
    }
    this.createError.set(null);
    await this.run(
      async () => {
        await this.api.invoke(apiV1SportsPost, { body: { name: this.newName().trim() } });
        this.newName.set('');
        this.toasts.success(this.text.created);
      },
      (problem) => this.createError.set(fieldErrors(problem)['name'] ?? problemMessage(problem)),
    );
  }

  protected startRename(sport: SportDto): void {
    this.editName.set(sport.name);
    this.renameError.set(null);
    this.editingId.set(sport.id);
    afterNextRender(() => this.element<HTMLInputElement>(`rename-${sport.id}`)?.select(), {
      injector: this.injector,
    });
  }

  protected cancelRename(): void {
    const id = this.editingId();
    this.editingId.set(null);
    this.renameError.set(null);
    if (id) {
      afterNextRender(() => this.element(`rename-btn-${id}`)?.focus(), {
        injector: this.injector,
      });
    }
  }

  protected async saveRename(sport: SportDto): Promise<void> {
    const invalid = nameError(this.editName());
    if (invalid) {
      this.renameError.set(invalid);
      return;
    }
    await this.run(
      async () => {
        await this.api.invoke(apiV1SportsIdPut, {
          id: sport.id,
          body: { name: this.editName().trim(), version: Number(sport.version) },
        });
        this.cancelRename();
        this.toasts.success(this.text.renamed);
      },
      async (problem) => {
        if (problem.code === 'Concurrency.Conflict') {
          // Reload so the retry carries the current version; the typed name stays in the input.
          this.renameError.set(this.common.conflictReloaded);
          await this.list.load();
          return;
        }
        this.renameError.set(fieldErrors(problem)['name'] ?? problemMessage(problem));
      },
    );
  }

  protected async archive(sport: SportDto): Promise<void> {
    await this.run(async () => {
      const ok = await this.confirmer.confirm({
        title: this.text.archiveTitle,
        message: this.text.archiveText,
        confirmLabel: this.actions.archive,
      });
      if (!ok) {
        return false;
      }
      await this.api.invoke(apiV1SportsIdArchivePost, { id: sport.id });
      this.toasts.success(this.text.archived);
      return true;
    });
  }

  protected async remove(sport: SportDto): Promise<void> {
    await this.run(async () => {
      const ok = await this.confirmer.confirm({
        title: this.text.deleteTitle,
        message: this.text.deleteText,
        confirmLabel: this.actions.delete,
        destructive: true,
      });
      if (!ok) {
        return false;
      }
      await this.api.invoke(apiV1SportsIdDelete, { id: sport.id });
      this.toasts.success(this.text.deleted);
      return true;
    });
  }

  /**
   * Runs one change at a time (buttons are disabled while busy), reloads the list afterwards
   * (unless the action returns false, e.g. a cancelled confirmation) and reports the Turkish
   * problem message on failure.
   */
  private async run(
    action: () => Promise<boolean | void>,
    onError?: (problem: ApiProblem) => void | Promise<void>,
  ): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      if ((await action()) !== false) {
        await this.list.load();
      }
    } catch (err) {
      const problem = toProblem(err);
      if (onError) {
        await onError(problem);
      } else {
        this.toasts.error(problemMessage(problem));
      }
    } finally {
      this.busy.set(false);
    }
  }

  private element<T extends HTMLElement = HTMLElement>(id: string): T | null {
    return this.host.nativeElement.querySelector<T>(`[id="${id}"]`);
  }
}
