import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import {
  LucideCalendarCheck,
  LucideChevronLeft,
  LucideChevronRight,
  LucideClipboardCheck,
  LucideMapPin,
} from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1MeSessionsGet } from '../../api/functions';
import { SessionDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatDate, formatTime } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { addDays, isoDate, weekdayName } from '../../core/schedule/schedule';

/**
 * US-12: the coach's sessions for one day, phone first (NFR-USA-01): large cards, 44px+ targets,
 * a prev/next day switcher. Attendance is a disabled placeholder until that feature lands.
 */
@Component({
  selector: 'app-coach-today-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    LucideCalendarCheck,
    LucideChevronLeft,
    LucideChevronRight,
    LucideClipboardCheck,
    LucideMapPin,
  ],
  template: `
    <div class="mx-auto w-full max-w-xl">
      <h1 class="sr-only">{{ text.title }}</h1>
      <nav
        class="card mb-4 flex items-center justify-between gap-2 p-2"
        [attr.aria-label]="text.title"
      >
        <button
          type="button"
          class="btn btn-ghost btn-icon size-12!"
          [attr.aria-label]="text.previousDay"
          (click)="shift(-1)"
        >
          <svg lucideChevronLeft class="size-6!"></svg>
        </button>
        <div class="min-w-0 text-center" aria-live="polite">
          <p class="text-lg font-bold">{{ heading() }}</p>
          <p class="font-mono text-sm text-muted-foreground">{{ dateText() }}</p>
        </div>
        <button
          type="button"
          class="btn btn-ghost btn-icon size-12!"
          [attr.aria-label]="text.nextDay"
          (click)="shift(1)"
        >
          <svg lucideChevronRight class="size-6!"></svg>
        </button>
      </nav>
      @if (!isToday()) {
        <button type="button" class="btn btn-secondary mb-4 h-12 w-full" (click)="goToday()">
          {{ text.today }}
        </button>
      }

      @if (error(); as message) {
        <div role="alert" class="card flex flex-col gap-3 p-4 text-sm text-destructive">
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary h-12" (click)="load()">
            {{ common.retry }}
          </button>
        </div>
      } @else if (sessions() === null) {
        <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
      } @else if (sessions()!.length === 0) {
        <div class="card flex flex-col items-center gap-3 px-6 py-12 text-center">
          <div class="grid size-12 place-items-center rounded-2xl bg-primary/10 text-primary">
            <svg lucideCalendarCheck class="size-6"></svg>
          </div>
          <p class="text-base text-muted-foreground">{{ text.empty }}</p>
        </div>
      } @else {
        <p class="sr-only" role="status">{{ count() }}</p>
        <ul class="space-y-3" [attr.aria-busy]="loading()" [class.opacity-60]="loading()">
          @for (session of sessions(); track session.groupId + session.start) {
            <li class="card p-4">
              <p class="font-mono text-2xl font-extrabold tracking-tight">
                {{ time(session.start) }}–{{ time(session.end) }}
              </p>
              <h2 class="mt-1 text-lg font-semibold">{{ session.groupName }}</h2>
              <p class="mt-1 flex items-center gap-1.5 text-base text-muted-foreground">
                <svg lucideMapPin class="size-4 shrink-0" aria-hidden="true"></svg>
                {{ session.facility.name }}
              </p>
              <!-- Placeholder until attendance (US-13) lands: focusable, explained, inert. -->
              <button
                type="button"
                class="btn btn-primary mt-4 h-12 w-full cursor-not-allowed opacity-50"
                aria-disabled="true"
                [attr.aria-describedby]="'soon-' + $index"
                [attr.title]="text.comingSoon"
              >
                <svg lucideClipboardCheck></svg>{{ text.takeAttendance }}
              </button>
              <p [id]="'soon-' + $index" class="mt-1 text-center text-xs text-muted-foreground">
                {{ text.comingSoon }}
              </p>
            </li>
          }
        </ul>
      }
    </div>
  `,
})
export class CoachTodayPage {
  private readonly api = inject(Api);

  protected readonly text = ui.coachToday;
  protected readonly common = ui.common;

  private readonly today = isoDate(new Date());
  protected readonly date = signal(this.today);
  protected readonly sessions = signal<SessionDto[] | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly isToday = computed(() => this.date() === this.today);
  protected readonly heading = computed(() =>
    this.isToday() ? this.text.title : weekdayName(this.date()),
  );
  protected readonly dateText = computed(() =>
    this.isToday()
      ? `${weekdayName(this.date())}, ${formatDate(this.date())}`
      : formatDate(this.date()),
  );
  protected readonly count = computed(() =>
    interpolate(this.text.sessionsCount, { count: this.sessions()?.length ?? 0 }),
  );
  private sequence = 0;

  constructor() {
    void this.load();
  }

  protected time(value: string): string {
    return formatTime(value);
  }

  protected shift(days: number): void {
    this.date.set(addDays(this.date(), days));
    void this.load();
  }

  protected goToday(): void {
    this.date.set(this.today);
    void this.load();
  }

  /** Only the latest day's response is shown when the coach taps quickly through days. */
  async load(): Promise<void> {
    const request = ++this.sequence;
    this.loading.set(true);
    this.error.set(null);
    try {
      const sessions = await this.api.invoke(apiV1MeSessionsGet, { date: this.date() });
      if (request === this.sequence) {
        this.sessions.set(sessions);
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
