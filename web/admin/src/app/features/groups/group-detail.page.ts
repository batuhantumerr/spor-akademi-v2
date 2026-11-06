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
import { LucideArrowLeft, LucideCalendarClock } from '@lucide/angular';

import { Api } from '../../api/api';
import {
  apiV1GroupsIdGet,
  apiV1GroupsIdSchedulePut,
  apiV1GroupsIdSessionsGet,
} from '../../api/functions';
import { GroupDto, SessionDto, WarningDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatDate, formatTime } from '../../core/format/format';
import { ui } from '../../core/i18n/tr';
import { SlotDraft, addDays, summarizeSchedule, weekdayName } from '../../core/schedule/schedule';
import { SchoolClock } from '../../core/time/school-clock';
import { ConflictState } from '../../shared/data/conflict-state';
import { ConflictNoticeComponent } from '../../shared/ui/conflict-notice.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { ToastService } from '../../shared/ui/toast.service';
import { ScheduleEditorComponent } from './schedule-editor.component';

const UPCOMING_DAYS = 14;

/** US-09: one group — key facts, weekly schedule editor and the next two weeks of sessions. */
@Component({
  selector: 'app-group-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    PageHeaderComponent,
    ScheduleEditorComponent,
    ConflictNoticeComponent,
    LucideArrowLeft,
    LucideCalendarClock,
  ],
  template: `
    <a routerLink="/groups" class="btn btn-ghost btn-sm mb-2 -ml-2">
      <svg lucideArrowLeft></svg>{{ text.back }}
    </a>

    @if (loadError(); as message) {
      <div
        role="alert"
        class="card flex items-center justify-between gap-3 p-4 text-sm text-destructive"
      >
        <span>{{ message }}</span>
        <button type="button" class="btn btn-secondary btn-sm" (click)="load()">
          {{ common.retry }}
        </button>
      </div>
    } @else if (!group()) {
      <p role="status" class="card p-6 text-sm text-muted-foreground">{{ common.loading }}</p>
    } @else {
      @let g = group()!;
      <app-page-header [title]="g.name" [description]="g.sport.name">
        <span
          class="badge"
          [class.badge-success]="g.status === 'Active'"
          [class.badge-muted]="g.status !== 'Active'"
        >
          {{ groups.statuses[g.status] }}
        </span>
      </app-page-header>

      <section class="card mb-4 p-4" aria-labelledby="facts-title">
        <h2 id="facts-title" class="sr-only">{{ text.facts }}</h2>
        <dl class="grid gap-4 text-sm sm:grid-cols-2 lg:grid-cols-5">
          <div>
            <dt class="text-xs font-bold tracking-wider text-muted-foreground uppercase">
              {{ groups.birthYears }}
            </dt>
            <dd class="mt-1 font-mono">{{ g.birthYearRange.from }}–{{ g.birthYearRange.to }}</dd>
          </div>
          <div>
            <dt class="text-xs font-bold tracking-wider text-muted-foreground uppercase">
              {{ groups.capacity }}
            </dt>
            <dd class="mt-1 font-mono">{{ g.capacity }}</dd>
          </div>
          <div>
            <dt class="text-xs font-bold tracking-wider text-muted-foreground uppercase">
              {{ groups.coach }}
            </dt>
            <dd class="mt-1">
              @if (g.coach?.isActive) {
                {{ g.coach!.firstName }} {{ g.coach!.lastName }}
              } @else {
                <span class="badge badge-warning">{{ groups.noCoach }}</span>
              }
            </dd>
          </div>
          <div>
            <dt class="text-xs font-bold tracking-wider text-muted-foreground uppercase">
              {{ groups.facility }}
            </dt>
            <dd class="mt-1">{{ g.facility.name }}</dd>
          </div>
          <div>
            <dt class="text-xs font-bold tracking-wider text-muted-foreground uppercase">
              {{ groups.schedule }}
            </dt>
            <dd class="mt-1">{{ summary() }}</dd>
          </div>
          @if (g.archivedOn) {
            <div>
              <dt class="text-xs font-bold tracking-wider text-muted-foreground uppercase">
                {{ text.archivedOn }}
              </dt>
              <dd class="mt-1 font-mono">{{ date(g.archivedOn) }}</dd>
            </div>
          }
        </dl>
      </section>

      <div class="grid gap-4 lg:grid-cols-[2fr_1fr]">
        <section class="card p-4" aria-labelledby="schedule-title">
          <h2 id="schedule-title" class="mb-2 text-base font-semibold">{{ groups.schedule }}</h2>
          @if (g.status !== 'Active') {
            <p class="mb-3 text-sm text-muted-foreground">{{ text.archivedNotice }}</p>
          }
          @if (conflictState.open()) {
            <div class="mb-3">
              <app-conflict-notice
                [ready]="conflictState.fresh() !== null"
                [loadError]="conflictState.error()"
                [busy]="saving()"
                (applyMine)="applyMine()"
                (loadServer)="loadServer()"
                (retry)="loadFresh()"
              />
            </div>
          }
          <app-schedule-editor
            [slots]="g.slots"
            [warnings]="warnings()"
            [error]="scheduleError()"
            [errorSlot]="errorSlot()"
            [saving]="saving()"
            [readonly]="g.status !== 'Active'"
            (save)="saveSchedule($event)"
          />
        </section>

        <section class="card p-4" aria-labelledby="upcoming-title">
          <h2 id="upcoming-title" class="text-base font-semibold">{{ text.upcoming }}</h2>
          <p class="text-xs text-muted-foreground">{{ text.upcomingHint }}</p>
          @if (sessionsError(); as message) {
            <p role="alert" class="mt-3 text-sm text-destructive">{{ message }}</p>
          } @else if (sessions() === null) {
            <p role="status" class="mt-3 text-sm text-muted-foreground">{{ common.loading }}</p>
          } @else if (sessions()!.length === 0) {
            <p class="mt-3 text-sm text-muted-foreground">{{ text.noUpcoming }}</p>
          } @else {
            <ul class="mt-3 divide-y divide-border/60">
              @for (session of sessions(); track session.date + session.start) {
                <li class="flex items-start gap-3 py-2 text-sm">
                  <svg
                    lucideCalendarClock
                    class="mt-0.5 size-4 shrink-0 text-primary"
                    aria-hidden="true"
                  ></svg>
                  <div>
                    <p class="font-medium">{{ weekday(session.date) }}, {{ date(session.date) }}</p>
                    <p class="font-mono text-xs text-muted-foreground">
                      {{ time(session.start) }}–{{ time(session.end) }} ·
                      <span class="font-sans">{{ session.facility.name }}</span>
                    </p>
                  </div>
                </li>
              }
            </ul>
          }
        </section>
      </div>
    }
  `,
})
export class GroupDetailPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(ToastService);

  /** Route parameter (component input binding). */
  readonly id = input.required<string>();

  protected readonly text = ui.groupDetail;
  protected readonly groups = ui.groups;
  protected readonly common = ui.common;

  protected readonly group = signal<GroupDto | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly sessions = signal<SessionDto[] | null>(null);
  protected readonly sessionsError = signal<string | null>(null);
  protected readonly warnings = signal<WarningDto[]>([]);
  protected readonly scheduleError = signal<string | null>(null);
  protected readonly errorSlot = signal<SlotDraft | null>(null);
  protected readonly saving = signal(false);
  /** After a 409: the user's unsaved slots stay in the editor; the fresh version loads aside. */
  protected readonly conflictState = new ConflictState<GroupDto>(() =>
    this.api.invoke(apiV1GroupsIdGet, { id: this.group()!.id }),
  );
  protected readonly summary = computed(() => summarizeSchedule(this.group()?.slots ?? []));

  private readonly clock = inject(SchoolClock);
  /** The slots of the last save attempt, re-sent by "apply my changes". */
  private pendingSlots: SlotDraft[] | null = null;
  private loadSequence = 0;
  private sessionsSequence = 0;

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => {
        this.reset();
        void this.load(id);
      });
    });
    // The upcoming window starts at the school's today; follow it when the day turns over.
    effect(() => {
      this.clock.today();
      untracked(() => {
        if (this.group()) {
          void this.loadSessions();
        }
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

  /** Loads the group of the current route; a response for an older id is ignored. */
  async load(id = this.id()): Promise<void> {
    const request = ++this.loadSequence;
    this.loadError.set(null);
    try {
      const group = await this.api.invoke(apiV1GroupsIdGet, { id });
      if (request !== this.loadSequence) {
        return;
      }
      this.group.set(group);
    } catch (err) {
      if (request === this.loadSequence) {
        this.loadError.set(problemMessage(toProblem(err)));
      }
      return;
    }
    await this.loadSessions();
  }

  protected async saveSchedule(slots: SlotDraft[]): Promise<void> {
    const group = this.group();
    if (!group || this.saving()) {
      return;
    }
    await this.put(group.id, slots, Number(group.version));
  }

  /** Re-send the user's slots on top of the fresh version. */
  protected async applyMine(): Promise<void> {
    const fresh = this.conflictState.fresh();
    const slots = this.pendingSlots;
    if (!fresh || !slots || this.saving()) {
      return;
    }
    await this.put(fresh.id, slots, Number(fresh.version));
  }

  /** Discard the user's slots and show the server's version. */
  protected loadServer(): void {
    const fresh = this.conflictState.fresh();
    if (!fresh) {
      return;
    }
    this.clearConflict();
    this.scheduleError.set(null);
    this.errorSlot.set(null);
    this.group.set(fresh);
  }

  /** Retry after a failed load of the server version. */
  protected loadFresh(): Promise<void> {
    return this.conflictState.load();
  }

  private async put(id: string, slots: SlotDraft[], version: number): Promise<void> {
    this.saving.set(true);
    this.scheduleError.set(null);
    this.errorSlot.set(null);
    this.pendingSlots = slots;
    try {
      const result = await this.api.invoke(apiV1GroupsIdSchedulePut, {
        id,
        body: { slots, version },
      });
      const base = this.conflictState.close() ?? this.group();
      if (base) {
        this.group.set({ ...base, slots: result.slots, version: result.version });
      }
      // Warnings are replaced only by a successful save; a failed one keeps the last notice.
      this.warnings.set(result.warnings);
      this.toasts.success(this.text.scheduleSaved);
      void this.loadSessions();
    } catch (err) {
      const problem = toProblem(err);
      if (problem.code === 'Concurrency.Conflict') {
        // Keep the user's slots in the editor; load the server's version next to them.
        await this.conflictState.start();
      } else {
        // e.g. Schedule.CoachUnavailable carries the other group's name; slots[i] names the row.
        this.scheduleError.set(problemMessage(problem));
        this.errorSlot.set(offendingSlot(problem.errors, slots));
      }
    } finally {
      this.saving.set(false);
    }
  }

  private async loadSessions(): Promise<void> {
    const request = ++this.sessionsSequence;
    this.sessionsError.set(null);
    const from = this.clock.today();
    try {
      const sessions = await this.api.invoke(apiV1GroupsIdSessionsGet, {
        id: this.id(),
        from,
        to: addDays(from, UPCOMING_DAYS - 1),
      });
      if (request === this.sessionsSequence) {
        this.sessions.set(sessions);
      }
    } catch (err) {
      if (request === this.sessionsSequence) {
        this.sessionsError.set(problemMessage(toProblem(err)));
      }
    }
  }

  private clearConflict(): void {
    this.conflictState.close();
    this.pendingSlots = null;
  }

  /** A new route id starts from a clean view, never showing the previous group. */
  private reset(): void {
    this.group.set(null);
    this.loadError.set(null);
    this.sessions.set(null);
    this.sessionsError.set(null);
    this.warnings.set([]);
    this.scheduleError.set(null);
    this.errorSlot.set(null);
    this.clearConflict();
    this.sessionsSequence++;
  }
}

/** The slot an API error names via a `slots[i]` field path, if any. */
function offendingSlot(
  errors: Record<string, string[]> | undefined,
  sent: SlotDraft[],
): SlotDraft | null {
  for (const key of Object.keys(errors ?? {})) {
    const match = /^slots\[(\d+)\]/.exec(key);
    if (match) {
      return sent[Number(match[1])] ?? null;
    }
  }
  return null;
}
