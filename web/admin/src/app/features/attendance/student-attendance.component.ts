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

import { Api } from '../../api/api';
import { apiV1StudentsIdAttendanceGet } from '../../api/functions';
import { StudentAttendanceDto, StudentSessionDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { STATUSES, isMark, statusTone } from '../../core/attendance/attendance';
import { formatDate, formatPercent, formatTime } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { addDays } from '../../core/schedule/schedule';
import { SchoolClock } from '../../core/time/school-clock';

/** US-21: a student's attendance over a date range, with the attendance rate. */
@Component({
  selector: 'app-student-attendance',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <section class="card p-4" aria-labelledby="student-attendance-title">
      <h2 id="student-attendance-title" class="mb-3 text-base font-semibold">
        {{ text.history }}
      </h2>
      <form
        class="mb-3 flex flex-wrap items-end gap-2"
        (submit)="$event.preventDefault(); apply()"
        novalidate
      >
        <div>
          <label class="field-label" for="att-from">{{ text.from }}</label>
          <input
            id="att-from"
            type="date"
            class="input"
            [max]="to()"
            [value]="from()"
            (input)="from.set($any($event.target).value)"
            aria-describedby="att-range-hint"
          />
        </div>
        <div>
          <label class="field-label" for="att-to">{{ text.to }}</label>
          <input
            id="att-to"
            type="date"
            class="input"
            [min]="from()"
            [value]="to()"
            (input)="to.set($any($event.target).value)"
            aria-describedby="att-range-hint"
          />
        </div>
        <button type="submit" class="btn btn-secondary">{{ text.show }}</button>
        <p id="att-range-hint" class="w-full text-xs text-muted-foreground">{{ text.rangeHint }}</p>
      </form>

      @if (error(); as message) {
        <div role="alert" class="flex items-center justify-between gap-3 text-sm text-destructive">
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary btn-sm" (click)="load()">
            {{ common.retry }}
          </button>
        </div>
      } @else if (!data()) {
        <p role="status" class="text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else {
        @let d = data()!;
        <div aria-live="polite" [attr.aria-busy]="loading()">
          <p class="text-sm">
            <span class="font-medium">{{ text.rate }}:</span>
            <span class="ml-1 font-mono text-base font-bold" data-testid="rate">{{ rate() }}</span>
          </p>
          <p class="mt-1 text-xs text-muted-foreground">{{ summary() }}</p>
        </div>
        @if (d.sessions.length === 0) {
          <p class="mt-3 text-sm text-muted-foreground">{{ text.noHistory }}</p>
        } @else {
          <ul class="mt-3 divide-y divide-border/60" [class.opacity-60]="loading()">
            @for (s of d.sessions; track s.groupId + s.sessionKey) {
              <li class="flex items-center justify-between gap-3 py-2 text-sm">
                <div class="min-w-0">
                  <a
                    class="font-medium text-primary underline-offset-4 hover:underline"
                    [routerLink]="['/groups', s.groupId]"
                    >{{ s.groupName }}</a
                  >
                  <p class="font-mono text-xs text-muted-foreground">
                    {{ date(s.date) }} · {{ time(s.start) }}
                  </p>
                </div>
                @if (mark(s); as status) {
                  <span class="badge shrink-0" [class]="tone[status]">{{ labels[status] }}</span>
                } @else {
                  <span class="badge badge-muted shrink-0">{{ text.notRecorded }}</span>
                }
              </li>
            }
          </ul>
        }
      }
    </section>
  `,
})
export class StudentAttendanceComponent {
  private readonly api = inject(Api);
  private readonly clock = inject(SchoolClock);

  readonly studentId = input.required<string>();

  protected readonly text = ui.attendance;
  protected readonly common = ui.common;
  protected readonly statuses = STATUSES;
  protected readonly labels = ui.attendance.statuses;
  protected readonly tone = statusTone;
  /** The last 30 days up to the school's today by default. */
  protected readonly to = signal(this.clock.today());
  protected readonly from = signal(addDays(this.clock.today(), -29));
  protected readonly data = signal<StudentAttendanceDto | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly rate = computed(() => {
    const rate = this.data()?.summary.rate;
    return rate === null || rate === undefined
      ? this.text.rateUnknown
      : formatPercent(Number(rate));
  });
  protected readonly summary = computed(() => {
    const s = this.data()?.summary;
    return s
      ? interpolate(this.text.summary, {
          sessions: s.sessions,
          present: s.present,
          late: s.late,
          absent: s.absent,
          excused: s.excused,
          notRecorded: s.notRecorded,
        })
      : '';
  });
  private sequence = 0;

  constructor() {
    effect(() => {
      this.studentId();
      untracked(() => void this.load());
    });
  }

  protected mark(session: StudentSessionDto) {
    return isMark(session.status) ? session.status : null;
  }

  protected date(iso: string): string {
    return formatDate(iso);
  }

  protected time(value: string): string {
    return formatTime(value);
  }

  protected apply(): void {
    void this.load();
  }

  async load(): Promise<void> {
    const request = ++this.sequence;
    this.loading.set(true);
    this.error.set(null);
    try {
      const data = await this.api.invoke(apiV1StudentsIdAttendanceGet, {
        id: this.studentId(),
        from: this.from(),
        to: this.to(),
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
