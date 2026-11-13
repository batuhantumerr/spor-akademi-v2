import { Dialog } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideBuilding, LucidePlus } from '@lucide/angular';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../api/api';
import { apiV1PlatformSchoolsGet } from '../../api/functions';
import { SchoolListItem } from '../../api/models';
import { ui } from '../../core/i18n/tr';
import { PagedList } from '../../shared/data/paged-list';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { TemporaryPasswordService } from '../../shared/ui/temporary-password.dialog';
import { ToastService } from '../../shared/ui/toast.service';
import { CreatedSchool, SCHOOL_FORM_TITLE_ID, SchoolFormDialog } from './school-form.dialog';

/** US-02: Platform Admin's list of schools; a new school comes with its first admin. */
@Component({
  selector: 'app-schools-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, PageHeaderComponent, PaginationComponent, LucideBuilding, LucidePlus],
  template: `
    <app-page-header [title]="text.title" [description]="text.description">
      <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="create()">
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
          <svg lucideBuilding class="size-6"></svg>
        </div>
        <p class="text-sm text-muted-foreground">{{ text.empty }}</p>
      </div>
    } @else if (list.items().length > 0) {
      <!-- Narrow screens scroll the table sideways inside its own frame (keyboard: focusable). -->
      <div
        class="table-shell transition-opacity"
        tabindex="0"
        role="region"
        aria-labelledby="schools-caption"
        [class.opacity-60]="list.refreshing()"
        [attr.aria-busy]="list.refreshing()"
      >
        <table class="data-table min-w-[40rem]">
          <caption id="schools-caption" class="sr-only">
            {{
              text.caption
            }}
          </caption>
          <thead>
            <tr>
              <th scope="col">{{ text.name }}</th>
              <th scope="col">{{ text.slug }}</th>
              <th scope="col">{{ text.status }}</th>
              <th scope="col">{{ text.admin }}</th>
            </tr>
          </thead>
          <tbody>
            @for (school of list.items(); track school.id) {
              <tr>
                <th scope="row">
                  <a
                    class="text-primary underline-offset-4 hover:underline"
                    [routerLink]="['/platform/schools', school.id]"
                    >{{ school.name }}</a
                  >
                  <span class="block text-xs font-normal text-muted-foreground">{{
                    school.timeZone
                  }}</span>
                </th>
                <td class="font-mono text-xs">{{ school.slug }}</td>
                <td>
                  <span
                    class="badge"
                    [class.badge-success]="school.status === 'Active'"
                    [class.badge-muted]="school.status !== 'Active'"
                  >
                    {{ text.statuses[school.status] }}
                  </span>
                </td>
                <td>
                  @if (school.admin; as admin) {
                    <span class="block">{{ admin.firstName }} {{ admin.lastName }}</span>
                    <span class="block text-xs font-normal break-all text-muted-foreground">{{
                      admin.email
                    }}</span>
                  } @else {
                    <span class="text-muted-foreground">{{ text.noAdmin }}</span>
                  }
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
export class SchoolsPage {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toasts = inject(ToastService);
  private readonly temporaryPassword = inject(TemporaryPasswordService);

  protected readonly text = ui.schools;
  protected readonly common = ui.common;
  protected readonly busy = signal(false);
  protected readonly list = new PagedList<SchoolListItem>((page) =>
    this.api.invoke(apiV1PlatformSchoolsGet, { page, pageSize: 25 }),
  );

  constructor() {
    void this.list.load();
  }

  protected async create(): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      const ref = this.dialog.open<CreatedSchool>(SchoolFormDialog, {
        ariaLabelledBy: SCHOOL_FORM_TITLE_ID,
        backdropClass: ['bg-slate-900/60'],
      });
      const created = await firstValueFrom(ref.closed);
      if (!created) {
        return;
      }
      this.toasts.success(this.text.created);
      void this.list.load();
      // The password goes straight into the one-time dialog; nothing here keeps it.
      const shown = this.temporaryPassword.show({
        name: `${created.adminName} (${created.schoolName})`,
        password: created.temporaryPassword,
      });
      created.temporaryPassword = '';
      await shown;
    } finally {
      this.busy.set(false);
    }
  }
}
