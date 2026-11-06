import { Dialog } from '@angular/cdk/dialog';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideUserPlus } from '@lucide/angular';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../api/api';
import { apiV1GroupsIdEnrollmentsGet } from '../../api/functions';
import { EnrollmentDto, EnrollmentStatus, GroupDto } from '../../api/models';
import { formatDate } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { PagedList } from '../../shared/data/paged-list';
import { PaginationComponent } from '../../shared/ui/pagination.component';
import { ToastService } from '../../shared/ui/toast.service';
import {
  END_ENROLLMENT_TITLE_ID,
  ENROLL_TITLE_ID,
  EndEnrollmentData,
  EndEnrollmentDialog,
  EnrollDialog,
  EnrollGroupData,
  activeCount,
} from './enrollment-dialogs';

type StatusFilter = EnrollmentStatus | '';

/** "Öğrenciler" on the group page: capacity bar, enrollments by status, enroll and end. */
@Component({
  selector: 'app-group-students',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, PaginationComponent, LucideUserPlus],
  template: `
    <section class="card p-4" aria-labelledby="group-students-title">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h2 id="group-students-title" class="text-base font-semibold">{{ text.students }}</h2>
        @if (group().status === 'Active') {
          <button
            type="button"
            class="btn btn-secondary btn-sm"
            [disabled]="busy()"
            (click)="enroll()"
          >
            <svg lucideUserPlus></svg>{{ text.enrollStudent }}
          </button>
        }
      </div>

      <div class="mb-4">
        <div class="flex items-baseline justify-between text-sm">
          <span id="capacity-label" class="font-medium">{{ text.capacity }}</span>
          <span class="font-mono" aria-hidden="true">{{ capacityText() }}</span>
        </div>
        <progress
          class="capacity-bar mt-1 h-2 w-full"
          [class.capacity-full]="full()"
          [value]="used() ?? 0"
          [max]="capacity()"
          [attr.aria-label]="capacityLabel()"
        ></progress>
      </div>

      <div class="mb-3 max-w-xs">
        <label class="field-label" for="enrollment-status">{{ text.statusFilter }}</label>
        <select
          id="enrollment-status"
          class="input"
          (change)="setStatus($any($event.target).value)"
        >
          <option value="Active" [selected]="status() === 'Active'">
            {{ text.statuses['Active'] }}
          </option>
          <option value="Ended" [selected]="status() === 'Ended'">
            {{ text.statuses['Ended'] }}
          </option>
          <option value="" [selected]="status() === ''">{{ text.allStatuses }}</option>
        </select>
      </div>

      @if (list.error(); as message) {
        <div
          role="alert"
          class="mb-3 flex items-center justify-between gap-3 text-sm text-destructive"
        >
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary btn-sm" (click)="list.load()">
            {{ common.retry }}
          </button>
        </div>
      }
      @if (list.loading()) {
        <p role="status" class="text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else if (list.isEmpty()) {
        <p class="text-sm text-muted-foreground">{{ text.noneInGroup }}</p>
      } @else if (list.items().length > 0) {
        <ul
          class="divide-y divide-border/60"
          [attr.aria-busy]="list.refreshing()"
          [class.opacity-60]="list.refreshing()"
        >
          @for (e of list.items(); track e.id) {
            <li class="flex flex-wrap items-center justify-between gap-2 py-2 text-sm">
              <div class="min-w-0">
                <a
                  class="font-medium text-primary underline-offset-4 hover:underline"
                  [routerLink]="['/students', e.studentId]"
                  >{{ e.studentFirstName }} {{ e.studentLastName }}</a
                >
                <p class="font-mono text-xs text-muted-foreground">
                  {{ date(e.startDate) }} – {{ e.endDate ? date(e.endDate) : '…' }}
                </p>
              </div>
              @if (e.status === 'Active') {
                <button
                  type="button"
                  class="btn btn-ghost btn-sm hover:!bg-destructive/10 hover:!text-destructive"
                  [attr.aria-label]="text.end + ': ' + e.studentFirstName + ' ' + e.studentLastName"
                  [disabled]="busy()"
                  (click)="end(e)"
                >
                  {{ text.end }}
                </button>
              } @else {
                <span class="badge badge-muted">{{ text.statuses[e.status] }}</span>
              }
            </li>
          }
        </ul>
        <app-pagination
          [page]="list.page()"
          [totalPages]="list.totalPages()"
          [totalCount]="list.totalCount()"
          [disabled]="list.refreshing()"
          (pageChange)="list.goTo($event)"
        />
      }
    </section>
  `,
})
export class GroupStudentsComponent {
  private readonly api = inject(Api);
  private readonly dialog = inject(Dialog);
  private readonly toasts = inject(ToastService);

  readonly group = input.required<GroupDto>();

  protected readonly text = ui.enrollments;
  protected readonly common = ui.common;
  protected readonly status = signal<StatusFilter>('Active');
  protected readonly busy = signal(false);
  /** Active enrollments (the used part of the capacity), whatever the list filter shows. */
  protected readonly used = signal<number | null>(null);
  protected readonly capacity = computed(() => Number(this.group().capacity));
  protected readonly full = computed(() => (this.used() ?? 0) >= this.capacity());
  protected readonly capacityText = computed(() =>
    interpolate(this.text.capacityValue, { used: this.used() ?? '–', capacity: this.capacity() }),
  );
  protected readonly capacityLabel = computed(() =>
    interpolate(this.text.capacityLabel, { used: this.used() ?? 0, capacity: this.capacity() }),
  );
  protected readonly list = new PagedList<EnrollmentDto>((page) =>
    this.api.invoke(apiV1GroupsIdEnrollmentsGet, {
      id: this.group().id,
      status: this.status() || undefined,
      page,
      pageSize: 25,
    }),
  );
  private readonly groupId = computed(() => this.group().id);
  private countSequence = 0;

  constructor() {
    effect(() => {
      this.groupId();
      untracked(() => void this.refresh(true));
    });
  }

  protected date(iso: string): string {
    return formatDate(iso);
  }

  protected setStatus(value: string): void {
    this.status.set(value as StatusFilter);
    void this.list.reset();
  }

  protected async enroll(): Promise<void> {
    await this.exclusive(async () => {
      const data: EnrollGroupData = { group: this.group() };
      const ref = this.dialog.open<EnrollmentDto>(EnrollDialog, {
        data,
        ariaLabelledBy: ENROLL_TITLE_ID,
        backdropClass: ['bg-slate-900/60'],
      });
      if (await firstValueFrom(ref.closed)) {
        this.toasts.success(this.text.enrolled);
        await this.refresh();
      }
    });
  }

  protected async end(enrollment: EnrollmentDto): Promise<void> {
    await this.exclusive(async () => {
      const data: EndEnrollmentData = { enrollment };
      const ref = this.dialog.open<EnrollmentDto>(EndEnrollmentDialog, {
        data,
        ariaLabelledBy: END_ENROLLMENT_TITLE_ID,
        backdropClass: ['bg-slate-900/60'],
      });
      if (await firstValueFrom(ref.closed)) {
        this.toasts.success(this.text.endedToast);
        await this.refresh();
      }
    });
  }

  private async refresh(reset = false): Promise<void> {
    await Promise.all([reset ? this.list.reset() : this.list.load(), this.loadCount()]);
  }

  private async loadCount(): Promise<void> {
    const request = ++this.countSequence;
    try {
      const used = await activeCount(this.api, this.group().id);
      if (request === this.countSequence) {
        this.used.set(used);
      }
    } catch {
      // The bar is informational; the list shows its own errors.
    }
  }

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
