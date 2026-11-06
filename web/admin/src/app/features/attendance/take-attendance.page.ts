import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { LucideArrowLeft, LucideCheckCheck } from '@lucide/angular';

import { Api } from '../../api/api';
import {
  apiV1GroupsIdSessionsSessionKeyAttendanceGet,
  apiV1GroupsIdSessionsSessionKeyAttendancePut,
} from '../../api/functions';
import { SessionAttendanceDto, SessionStudentDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { AuthService } from '../../core/auth/auth.service';
import { Mark, STATUSES, isMark, statusTone } from '../../core/attendance/attendance';
import { formatDate, formatTime } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { weekdayName } from '../../core/schedule/schedule';
import { ToastService } from '../../shared/ui/toast.service';

/**
 * Take or correct the attendance of one session (US-13, NFR-PRF-03: 20 students in under 30 s on
 * a phone). One row per student with four large choices, "everyone present" in one tap, a sticky
 * save bar and a single PUT. Selections survive a failed save, so a dropped connection costs a
 * retry, not the work.
 */
@Component({
  selector: 'app-take-attendance-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, LucideArrowLeft, LucideCheckCheck],
  template: `
    <div class="mx-auto w-full max-w-xl pb-28">
      <a [routerLink]="backLink()" class="btn btn-ghost mb-2 -ml-2 h-11">
        <svg lucideArrowLeft></svg>{{ backLabel() }}
      </a>

      @if (loadError(); as message) {
        <div role="alert" class="card flex flex-col gap-3 p-4 text-sm text-destructive">
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary h-12" (click)="load()">
            {{ text.retry }}
          </button>
        </div>
      } @else if (!session()) {
        <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else {
        @let s = session()!;
        <h1 class="text-xl font-bold tracking-tight">{{ text.title }}</h1>
        <p class="mb-3 text-base text-muted-foreground">
          {{ weekday(s.date) }}, {{ date(s.date) }} ·
          <span class="font-mono">{{ time(s.start) }}–{{ time(s.end) }}</span>
        </p>
        @if (s.isRecorded) {
          <p class="mb-3 text-sm text-muted-foreground">{{ text.recorded }}</p>
        }

        @if (saveError(); as message) {
          <div
            id="save-error"
            role="alert"
            tabindex="-1"
            class="mb-3 flex flex-col gap-2 rounded-lg border border-destructive/30 bg-destructive/10 p-3 text-sm text-destructive"
          >
            <span>{{ message }}</span>
            @if (offline()) {
              <button type="button" class="btn btn-secondary h-12" (click)="save()">
                {{ text.retry }}
              </button>
            }
          </div>
        }

        @if (s.students.length === 0) {
          <p class="card p-6 text-base text-muted-foreground">{{ text.noStudents }}</p>
        } @else {
          <button type="button" class="btn btn-secondary mb-3 h-12 w-full" (click)="allPresent()">
            <svg lucideCheckCheck></svg>{{ text.allPresent }}
          </button>
          <ul class="space-y-2">
            @for (student of s.students; track student.studentId) {
              <li>
                <fieldset class="card min-w-0 p-3">
                  <legend class="sr-only">{{ statusFor(student) }}</legend>
                  <p class="mb-2 text-base font-semibold" aria-hidden="true">
                    {{ student.firstName }} {{ student.lastName }}
                  </p>
                  <div class="grid grid-cols-4 gap-1.5">
                    @for (status of statuses; track status) {
                      <label
                        class="relative flex min-h-12 cursor-pointer items-center justify-center rounded-lg border px-1 text-center text-sm font-semibold select-none has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-primary/50"
                        [class]="
                          marks()[student.studentId] === status
                            ? tone[status]
                            : 'border-border text-muted-foreground'
                        "
                      >
                        <input
                          type="radio"
                          class="sr-only"
                          [name]="'mark-' + student.studentId"
                          [value]="status"
                          [checked]="marks()[student.studentId] === status"
                          (change)="mark(student.studentId, status)"
                        />
                        {{ labels[status] }}
                      </label>
                    }
                  </div>
                </fieldset>
              </li>
            }
          </ul>

          <!-- Sticky save bar: always reachable with a thumb, whatever the scroll position. -->
          <div
            class="fixed inset-x-0 bottom-0 z-20 border-t border-border bg-card/95 p-3 backdrop-blur lg:left-auto lg:w-[calc(100%-16rem)]"
          >
            <div class="mx-auto flex max-w-xl items-center justify-between gap-3">
              <p class="text-sm font-medium" aria-live="polite">{{ markedText() }}</p>
              <button
                type="button"
                class="btn btn-primary h-12 px-6"
                [disabled]="saving() || markedCount() === 0"
                (click)="save()"
              >
                {{ saving() ? text.saving : text.save }}
              </button>
            </div>
          </div>
        }
      }
    </div>
  `,
})
export class TakeAttendancePage {
  private readonly api = inject(Api);
  private readonly toasts = inject(ToastService);
  private readonly auth = inject(AuthService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  /** Route parameters (component input binding). */
  readonly groupId = input.required<string>();
  readonly sessionKey = input.required<string>();

  protected readonly text = ui.attendance;
  protected readonly common = ui.common;
  protected readonly statuses = STATUSES;
  protected readonly labels = ui.attendance.statuses;
  protected readonly tone = statusTone;

  protected readonly session = signal<SessionAttendanceDto | null>(null);
  protected readonly loadError = signal<string | null>(null);
  /** The coach's current choices, by student id; kept through failed saves. */
  protected readonly marks = signal<Record<string, Mark>>({});
  protected readonly saving = signal(false);
  protected readonly saveError = signal<string | null>(null);
  protected readonly offline = signal(false);

  protected readonly markedCount = computed(() => {
    const ids = new Set(this.session()?.students.map((s) => s.studentId) ?? []);
    return Object.keys(this.marks()).filter((id) => ids.has(id)).length;
  });
  protected readonly markedText = computed(() =>
    interpolate(this.text.marked, {
      marked: this.markedCount(),
      total: this.session()?.students.length ?? 0,
    }),
  );
  protected readonly backLink = computed(() =>
    this.auth.role() === 'Coach' ? '/coach/today' : `/groups/${this.groupId()}`,
  );
  protected readonly backLabel = computed(() =>
    this.auth.role() === 'Coach' ? this.text.back : this.text.backToGroup,
  );
  private sequence = 0;

  constructor() {
    effect(() => {
      this.groupId();
      this.sessionKey();
      untracked(() => {
        this.session.set(null);
        this.marks.set({});
        this.saveError.set(null);
        void this.load();
      });
    });
  }

  protected date(iso: string): string {
    return formatDate(iso);
  }

  protected time(value: string): string {
    return formatTime(value);
  }

  protected weekday(iso: string): string {
    return weekdayName(iso);
  }

  protected statusFor(student: SessionStudentDto): string {
    return interpolate(this.text.statusFor, { name: `${student.firstName} ${student.lastName}` });
  }

  async load(): Promise<void> {
    const request = ++this.sequence;
    this.loadError.set(null);
    try {
      const session = await this.api.invoke(apiV1GroupsIdSessionsSessionKeyAttendanceGet, {
        id: this.groupId(),
        sessionKey: this.sessionKey(),
      });
      if (request === this.sequence) {
        this.apply(session);
      }
    } catch (err) {
      if (request === this.sequence) {
        this.loadError.set(problemMessage(toProblem(err)));
      }
    }
  }

  protected mark(studentId: string, status: Mark): void {
    this.marks.update((all) => ({ ...all, [studentId]: status }));
  }

  /** One tap for the common case; individual rows can then be changed. */
  protected allPresent(): void {
    const students = this.session()?.students ?? [];
    this.marks.set(Object.fromEntries(students.map((s) => [s.studentId, 'Present' as Mark])));
  }

  protected async save(): Promise<void> {
    const session = this.session();
    if (!session || this.saving()) {
      return;
    }
    const marks = this.marks();
    const records = session.students
      .filter((s) => marks[s.studentId])
      .map((s) => ({ studentId: s.studentId, status: marks[s.studentId] }));
    this.saving.set(true);
    this.saveError.set(null);
    this.offline.set(false);
    try {
      const saved = await this.api.invoke(apiV1GroupsIdSessionsSessionKeyAttendancePut, {
        id: this.groupId(),
        sessionKey: this.sessionKey(),
        body: { records },
      });
      this.apply(saved);
      this.toasts.success(this.text.saved);
    } catch (err) {
      const problem = toProblem(err);
      // A dropped connection keeps every selection; the retry sends the same marks again.
      this.offline.set(problem.status === 0);
      this.saveError.set(problem.status === 0 ? this.text.offline : problemMessage(problem));
      afterNextRender(
        () => this.host.nativeElement.querySelector<HTMLElement>('#save-error')?.focus(),
        { injector: this.injector },
      );
    } finally {
      this.saving.set(false);
    }
  }

  /** Shows the server's state: saved statuses become the current choices. */
  private apply(session: SessionAttendanceDto): void {
    this.session.set(session);
    this.marks.set(
      Object.fromEntries(
        session.students
          .filter((s) => isMark(s.status))
          .map((s) => [s.studentId, s.status as Mark]),
      ),
    );
  }
}
