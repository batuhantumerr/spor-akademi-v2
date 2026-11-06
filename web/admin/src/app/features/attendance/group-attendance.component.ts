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
import { LucideChevronLeft, LucideChevronRight } from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1GroupsIdAttendanceGet } from '../../api/functions';
import { GroupAttendanceMonthDto, MonthCellDto, MonthSessionDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { STATUSES, isMark, statusTone } from '../../core/attendance/attendance';
import { formatTime } from '../../core/format/format';
import { LOCALE } from '../../core/i18n/locale';
import { interpolate, ui } from '../../core/i18n/tr';
import { SchoolClock } from '../../core/time/school-clock';

const monthFormatter = new Intl.DateTimeFormat(LOCALE, { month: 'long', year: 'numeric' });

/** "2027-01" ± n months. */
export function addMonths(month: string, n: number): string {
  const [y, m] = month.split('-').map(Number);
  const d = new Date(y, m - 1 + n, 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}

/** US-21: the group's month matrix; a session column opens that session for editing. */
@Component({
  selector: 'app-group-attendance',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, LucideChevronLeft, LucideChevronRight],
  template: `
    <section class="card p-4" aria-labelledby="group-attendance-title">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h2 id="group-attendance-title" class="text-base font-semibold">{{ text.title }}</h2>
        <div class="flex items-center gap-1" role="group" [attr.aria-label]="text.month">
          <button
            type="button"
            class="btn btn-ghost btn-sm btn-icon"
            [attr.aria-label]="text.previousMonth"
            (click)="shift(-1)"
          >
            <svg lucideChevronLeft></svg>
          </button>
          <span class="min-w-32 text-center text-sm font-medium" aria-live="polite">
            {{ monthLabel() }}
          </span>
          <button
            type="button"
            class="btn btn-ghost btn-sm btn-icon"
            [attr.aria-label]="text.nextMonth"
            (click)="shift(1)"
          >
            <svg lucideChevronRight></svg>
          </button>
        </div>
      </div>

      <!-- Legend -->
      <ul class="mb-3 flex flex-wrap gap-2 text-xs" [attr.aria-label]="text.legend">
        @for (status of statuses; track status) {
          <li class="flex items-center gap-1">
            <span
              class="grid size-6 place-items-center rounded border font-bold"
              [class]="tone[status]"
              aria-hidden="true"
              >{{ symbols[status] }}</span
            >
            {{ labels[status] }}
          </li>
        }
        <li class="flex items-center gap-1">
          <span
            class="grid size-6 place-items-center rounded border border-border"
            aria-hidden="true"
            >–</span
          >
          {{ text.notRecorded }}
        </li>
        <li class="flex items-center gap-1">
          <span
            class="cell-not-enrolled size-6 rounded border border-dashed border-border"
            aria-hidden="true"
          ></span>
          {{ text.notEnrolled }}
        </li>
      </ul>

      @if (error(); as message) {
        <div role="alert" class="flex items-center justify-between gap-3 text-sm text-destructive">
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary btn-sm" (click)="load()">
            {{ common.retry }}
          </button>
        </div>
      } @else if (!data()) {
        <p role="status" class="text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else if (data()!.sessions.length === 0) {
        <p class="text-sm text-muted-foreground">{{ text.noSessions }}</p>
      } @else {
        @let d = data()!;
        <div
          class="table-shell"
          tabindex="0"
          role="region"
          aria-labelledby="attendance-caption"
          [attr.aria-busy]="loading()"
          [class.opacity-60]="loading()"
        >
          <table class="data-table w-auto">
            <caption id="attendance-caption" class="sr-only">
              {{
                caption()
              }}
            </caption>
            <thead>
              <tr>
                <th scope="col" class="sticky left-0 bg-muted">{{ text.student }}</th>
                @for (session of d.sessions; track session.sessionKey) {
                  <th scope="col" class="!px-1 text-center">
                    <a
                      class="block rounded px-1 py-1 text-primary underline-offset-2 hover:underline"
                      [routerLink]="['/groups', groupId(), 'sessions', session.sessionKey]"
                      [attr.aria-label]="editLabel(session)"
                      [attr.title]="editLabel(session)"
                    >
                      {{ day(session.date) }}
                      <span class="block text-xs font-normal normal-case">{{
                        time(session.start)
                      }}</span>
                    </a>
                  </th>
                }
              </tr>
            </thead>
            <tbody>
              @for (row of d.students; track row.studentId) {
                <tr>
                  <th scope="row" class="sticky left-0 bg-card">
                    {{ row.firstName }} {{ row.lastName }}
                  </th>
                  @for (cell of row.cells; track $index) {
                    <td class="!px-1 !py-1 text-center">
                      @if (!cell.enrolled) {
                        <span
                          class="cell-not-enrolled mx-auto block size-8 rounded border border-dashed border-border"
                          [attr.aria-label]="text.notEnrolled"
                          role="img"
                        ></span>
                      } @else if (mark(cell); as status) {
                        <span
                          class="mx-auto grid size-8 place-items-center rounded border font-bold"
                          [class]="tone[status]"
                          role="img"
                          [attr.aria-label]="labels[status]"
                          >{{ symbols[status] }}</span
                        >
                      } @else {
                        <span
                          class="mx-auto grid size-8 place-items-center rounded border border-border text-muted-foreground"
                          role="img"
                          [attr.aria-label]="text.notRecorded"
                          >–</span
                        >
                      }
                    </td>
                  }
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
})
export class GroupAttendanceComponent {
  private readonly api = inject(Api);
  private readonly clock = inject(SchoolClock);

  readonly groupId = input.required<string>();

  protected readonly text = ui.attendance;
  protected readonly common = ui.common;
  protected readonly statuses = STATUSES;
  protected readonly labels = ui.attendance.statuses;
  protected readonly symbols = ui.attendance.symbols;
  protected readonly tone = statusTone;

  /** The school's current month by default. */
  protected readonly month = signal(this.clock.today().slice(0, 7));
  protected readonly data = signal<GroupAttendanceMonthDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly monthLabel = computed(() => {
    const [y, m] = this.month().split('-').map(Number);
    return monthFormatter.format(new Date(y, m - 1, 1));
  });
  protected readonly caption = computed(() =>
    interpolate(this.text.matrixCaption, { month: this.monthLabel() }),
  );
  private sequence = 0;

  constructor() {
    effect(() => {
      this.groupId();
      this.month();
      untracked(() => void this.load());
    });
  }

  protected shift(n: number): void {
    this.month.set(addMonths(this.month(), n));
  }

  protected mark(cell: MonthCellDto) {
    return isMark(cell.status) ? cell.status : null;
  }

  protected day(date: string): string {
    return date.slice(8, 10);
  }

  protected time(value: string): string {
    return formatTime(value);
  }

  protected editLabel(session: MonthSessionDto): string {
    const [y, m, d] = session.date.split('-');
    return interpolate(this.text.editSession, {
      date: `${d}.${m}.${y}`,
      time: formatTime(session.start),
    });
  }

  async load(): Promise<void> {
    const request = ++this.sequence;
    this.loading.set(true);
    this.error.set(null);
    try {
      const data = await this.api.invoke(apiV1GroupsIdAttendanceGet, {
        id: this.groupId(),
        month: this.month(),
      });
      if (request === this.sequence) {
        this.data.set(data);
      }
    } catch (err) {
      if (request === this.sequence) {
        this.error.set(problemMessage(toProblem(err)));
      }
    } finally {
      if (request === this.sequence) {
        this.loading.set(false);
      }
    }
  }
}
