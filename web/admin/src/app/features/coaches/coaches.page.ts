import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import {
  LucideKeyRound,
  LucidePencil,
  LucidePlus,
  LucideUserX,
  LucideWhistle,
} from '@lucide/angular';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../api/api';
import {
  apiV1CoachesGet,
  apiV1CoachesIdDeactivatePost,
  apiV1CoachesIdResetPasswordPost,
} from '../../api/functions';
import { CoachDto, TemporaryPasswordIssued } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatPhone } from '../../core/format/format';
import { ui } from '../../core/i18n/tr';
import { PagedList } from '../../shared/data/paged-list';
import { ConfirmService } from '../../shared/ui/confirm-dialog.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { TemporaryPasswordService } from '../../shared/ui/temporary-password.dialog';
import { ToastService } from '../../shared/ui/toast.service';
import { COACH_FORM_TITLE_ID, CoachFormDialog, CoachFormResult } from './coach-form.dialog';

/** US-04: the school's coaches — add, edit, deactivate, reset password. */
@Component({
  selector: 'app-coaches-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    PageHeaderComponent,
    PaginationComponent,
    LucideKeyRound,
    LucidePencil,
    LucidePlus,
    LucideUserX,
    LucideWhistle,
  ],
  template: `
    <app-page-header [title]="text.title" [description]="text.description">
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
          <svg lucideWhistle class="size-6"></svg>
        </div>
        <p class="text-sm text-muted-foreground">{{ text.empty }}</p>
      </div>
    } @else if (list.items().length > 0) {
      <div
        class="table-shell transition-opacity"
        tabindex="0"
        role="region"
        aria-labelledby="coaches-caption"
        [class.opacity-60]="list.refreshing()"
        [attr.aria-busy]="list.refreshing()"
      >
        <table class="data-table min-w-[44rem]">
          <caption id="coaches-caption" class="sr-only">
            {{
              text.caption
            }}
          </caption>
          <thead>
            <tr>
              <th scope="col">{{ text.name }}</th>
              <th scope="col">{{ text.email }}</th>
              <th scope="col">{{ text.phoneShort }}</th>
              <th scope="col">{{ text.status }}</th>
              <th scope="col" class="text-right">{{ table.actions }}</th>
            </tr>
          </thead>
          <tbody>
            @for (coach of list.items(); track coach.id) {
              <tr [class.opacity-70]="coach.status !== 'Active'">
                <th scope="row">{{ coach.firstName }} {{ coach.lastName }}</th>
                <td class="break-all">{{ coach.email }}</td>
                <td class="font-mono text-xs whitespace-nowrap">{{ phone(coach.phone) }}</td>
                <td>
                  <span
                    class="badge"
                    [class.badge-success]="coach.status === 'Active'"
                    [class.badge-muted]="coach.status !== 'Active'"
                  >
                    {{ text.statuses[coach.status] }}
                  </span>
                </td>
                <td>
                  <div class="flex justify-end gap-1">
                    <button
                      type="button"
                      class="btn btn-ghost btn-sm btn-icon"
                      [attr.aria-label]="actions.edit + ': ' + fullName(coach)"
                      [attr.title]="actions.edit"
                      [disabled]="busy()"
                      (click)="openForm(coach)"
                    >
                      <svg lucidePencil></svg>
                    </button>
                    @if (coach.status === 'Active') {
                      <button
                        type="button"
                        class="btn btn-ghost btn-sm btn-icon"
                        [attr.aria-label]="text.resetPassword + ': ' + fullName(coach)"
                        [attr.title]="text.resetPassword"
                        [disabled]="busy()"
                        (click)="resetPassword(coach)"
                      >
                        <svg lucideKeyRound></svg>
                      </button>
                      <button
                        type="button"
                        class="btn btn-ghost btn-sm btn-icon hover:!bg-destructive/10 hover:!text-destructive"
                        [attr.aria-label]="text.deactivate + ': ' + fullName(coach)"
                        [attr.title]="text.deactivate"
                        [disabled]="busy()"
                        (click)="deactivate(coach)"
                      >
                        <svg lucideUserX></svg>
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
export class CoachesPage {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toasts = inject(ToastService);
  private readonly confirmer = inject(ConfirmService);
  private readonly temporaryPassword = inject(TemporaryPasswordService);

  protected readonly text = ui.coaches;
  protected readonly table = ui.table;
  protected readonly actions = ui.actions;
  protected readonly common = ui.common;
  protected readonly busy = signal(false);
  protected readonly list = new PagedList<CoachDto>((page) =>
    this.api.invoke(apiV1CoachesGet, { page, pageSize: 25 }),
  );

  constructor() {
    void this.list.load();
  }

  protected phone(value: string | null): string {
    return formatPhone(value);
  }

  protected fullName(coach: CoachDto): string {
    return `${coach.firstName} ${coach.lastName}`;
  }

  protected async openForm(coach: CoachDto | null): Promise<void> {
    await this.exclusive(async () => {
      const ref = this.dialog.open<CoachFormResult, CoachDto | null>(CoachFormDialog, {
        data: coach,
        ariaLabelledBy: COACH_FORM_TITLE_ID,
        backdropClass: ['bg-slate-900/60'],
      });
      const result = await firstValueFrom(ref.closed);
      // Reload either way: a version conflict inside the dialog may have left the list stale.
      void this.list.load();
      if (result?.kind === 'created') {
        this.toasts.success(this.text.created);
        // Straight into the one-time dialog; the source field is cleared right away.
        const shown = this.temporaryPassword.show({
          name: result.name,
          password: result.temporaryPassword,
        });
        result.temporaryPassword = '';
        await shown;
      } else if (result?.kind === 'saved') {
        this.toasts.success(this.text.saved);
      }
    });
  }

  protected async deactivate(coach: CoachDto): Promise<void> {
    await this.exclusive(async () => {
      const ok = await this.confirmer.confirm({
        title: this.text.deactivateTitle,
        message: this.text.deactivateText,
        confirmLabel: this.text.deactivate,
        destructive: true,
      });
      if (!ok) {
        return;
      }
      try {
        await this.api.invoke(apiV1CoachesIdDeactivatePost, { id: coach.id });
        this.toasts.success(this.text.deactivated);
      } catch (err) {
        this.toasts.error(problemMessage(toProblem(err)));
      }
      await this.list.load();
    });
  }

  protected async resetPassword(coach: CoachDto): Promise<void> {
    await this.exclusive(async () => {
      const ok = await this.confirmer.confirm({
        title: this.text.resetTitle,
        message: this.text.resetText,
        confirmLabel: this.text.resetPassword,
        destructive: true,
      });
      if (!ok) {
        return;
      }
      let issued: TemporaryPasswordIssued;
      try {
        issued = await this.api.invoke(apiV1CoachesIdResetPasswordPost, { id: coach.id });
      } catch (err) {
        this.toasts.error(problemMessage(toProblem(err)));
        return;
      }
      try {
        const shown = this.temporaryPassword.show({
          name: this.fullName(coach),
          password: issued.temporaryPassword,
        });
        issued.temporaryPassword = '';
        await shown;
      } catch {
        // The old password is already revoked; the only way forward is another reset.
        issued.temporaryPassword = '';
        this.toasts.error(ui.temporaryPassword.shownFailed);
      }
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
