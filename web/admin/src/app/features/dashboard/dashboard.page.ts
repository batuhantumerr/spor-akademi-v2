import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  LucideCalendarCheck,
  LucideChartPie,
  LucideGauge,
  LucideGraduationCap,
  LucideHandCoins,
  LucideIconInput,
  LucidePercent,
  LucideReceipt,
  LucideUsers,
  LucideWallet,
} from '@lucide/angular';

import { Api } from '../../api/api';
import { apiV1DashboardGet } from '../../api/functions';
import { DashboardDto } from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatDate, formatNumber } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { displayMoney } from '../../core/money/money';
import { ThemeService } from '../../core/theme/theme.service';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { StatCardComponent, StatTone } from '../../shared/ui/stat-card.component';
import {
  attendanceOption,
  attendanceSummary,
  billingOption,
  billingSummary,
  chartTheme,
  count,
  isZero,
  noEnrollments,
  percentText,
  periodLabel,
  ratio,
  sportsOption,
  sportName,
  sportsSummary,
  weekRange,
} from './dashboard-charts';
import { DashboardChartComponent } from './dashboard-chart.component';

interface Kpi {
  key: string;
  label: string;
  value: string;
  hint?: string;
  icon: LucideIconInput;
  tone: StatTone;
}

/** US-25: the School Admin's home page, one request (DEC-210), charts per docs/ui-tema.md. */
@Component({
  selector: 'app-dashboard-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DashboardChartComponent,
    LucideCalendarCheck,
    LucideChartPie,
    PageHeaderComponent,
    RouterLink,
    StatCardComponent,
  ],
  template: `
    <app-page-header [title]="nav.dashboard" [description]="description()" />

    @if (error(); as message) {
      <div role="alert" class="card flex flex-col items-start gap-3 p-4 text-sm text-destructive">
        <span>{{ message }}</span>
        <button type="button" class="btn btn-secondary" (click)="load()">
          {{ common.retry }}
        </button>
      </div>
    } @else if (data() === null) {
      <div aria-busy="true" data-testid="dashboard-skeleton">
        <p role="status" class="sr-only">{{ text.loading }}</p>
        <div class="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
          @for (i of skeletonCards; track i) {
            <div class="h-28 animate-pulse rounded-3xl border border-border bg-muted/60"></div>
          }
        </div>
        <div class="mt-4 grid grid-cols-1 gap-4 lg:grid-cols-2">
          @for (i of skeletonCharts; track i) {
            <div class="h-80 animate-pulse rounded-xl border border-border bg-muted/60"></div>
          }
        </div>
      </div>
    } @else if (isEmpty()) {
      <div
        class="card flex flex-col items-center gap-3 px-6 py-16 text-center"
        data-testid="dashboard-empty"
      >
        <div class="grid size-12 place-items-center rounded-2xl bg-primary/10 text-primary">
          <svg lucideChartPie class="size-6"></svg>
        </div>
        <h2 class="text-base font-semibold">{{ text.empty.title }}</h2>
        <p class="max-w-md text-sm text-muted-foreground">{{ text.empty.text }}</p>
        <div class="mt-2 flex flex-wrap justify-center gap-2">
          <a routerLink="/groups" class="btn btn-secondary">{{ text.empty.groups }}</a>
          <a routerLink="/students/new" class="btn btn-primary">{{ text.empty.student }}</a>
        </div>
      </div>
    } @else {
      <div [attr.aria-busy]="loading()" [class.opacity-60]="loading()">
        <ul
          class="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4"
          [attr.aria-label]="nav.dashboard"
        >
          @for (kpi of kpis(); track kpi.key) {
            <li [attr.data-kpi]="kpi.key">
              <app-stat-card
                [label]="kpi.label"
                [value]="kpi.value"
                [hint]="kpi.hint"
                [icon]="kpi.icon"
                [color]="kpi.tone"
              />
            </li>
          }
          <li data-kpi="today">
            <section
              class="relative flex h-full flex-col overflow-hidden rounded-3xl border border-border bg-card p-4 shadow-sm"
              aria-labelledby="today-title"
            >
              <div class="flex items-start justify-between gap-3">
                <h2
                  id="today-title"
                  class="text-xs font-bold tracking-wider text-muted-foreground uppercase"
                >
                  {{ text.today.title }}
                </h2>
                <div
                  class="grid size-8 place-items-center rounded-xl border border-border bg-muted/70 text-info"
                >
                  <svg lucideCalendarCheck class="size-4"></svg>
                </div>
              </div>
              @if (today().planned === 0) {
                <p class="mt-3 text-sm text-muted-foreground">{{ text.today.none }}</p>
              } @else {
                <p class="mt-3 font-mono text-2xl font-extrabold tracking-tight">
                  {{ today().value }}
                </p>
                <p class="mt-1 text-xs text-muted-foreground">{{ text.today.hint }}</p>
                <div class="mt-2 h-1.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
                  <div class="h-full rounded-full bg-info" [style.width.%]="today().percent"></div>
                </div>
              }
              <a
                routerLink="/groups"
                class="mt-auto pt-3 text-sm font-semibold text-primary hover:underline"
              >
                {{ text.today.link }}
              </a>
            </section>
          </li>
        </ul>

        <div class="mt-4 grid grid-cols-1 gap-4 lg:grid-cols-2">
          <app-dashboard-chart
            key="billing"
            [heading]="text.billing.title"
            [subtitle]="text.billing.subtitle"
            [summary]="billing().summary"
            [options]="billing().options"
            [columns]="billing().columns"
            [rows]="billing().rows"
          />
          <app-dashboard-chart
            key="attendance"
            [heading]="text.attendance.title"
            [subtitle]="text.attendance.subtitle"
            [summary]="attendance().summary"
            [options]="attendance().options"
            [columns]="attendance().columns"
            [rows]="attendance().rows"
          />
          <app-dashboard-chart
            key="sports"
            [heading]="text.sports.title"
            [subtitle]="text.sports.subtitle"
            [summary]="sports().summary"
            [options]="sports().options"
            [columns]="sports().columns"
            [rows]="sports().rows"
            [empty]="sports().empty ? text.sports.empty : null"
          />
          <section class="card flex min-w-0 flex-col" aria-labelledby="groups-title">
            <div class="px-4 pt-4">
              <h2 id="groups-title" class="text-sm font-semibold">{{ text.groups.title }}</h2>
              <p class="text-xs text-muted-foreground">{{ text.groups.subtitle }}</p>
            </div>
            @if (groups().length === 0) {
              <p class="grid min-h-40 flex-1 place-items-center p-4 text-sm text-muted-foreground">
                {{ text.groups.empty }}
              </p>
            } @else {
              <ol class="space-y-3 p-4" data-testid="group-utilisation">
                @for (group of groups(); track group.id) {
                  <li>
                    <div class="flex items-baseline justify-between gap-3 text-sm">
                      <a
                        [routerLink]="['/groups', group.id]"
                        class="min-w-0 font-medium break-words hover:text-primary hover:underline"
                        >{{ group.name }}</a
                      >
                      <span class="shrink-0 font-mono text-xs text-muted-foreground">
                        {{ group.fill }} ·
                        <span class="font-semibold text-foreground">{{ group.percent }}</span>
                        @if (group.full) {
                          <span class="badge ml-1 border-chart-5/30 bg-chart-5/10 text-chart-5">{{
                            text.groups.full
                          }}</span>
                        }
                      </span>
                    </div>
                    <div
                      class="mt-1.5 h-2 overflow-hidden rounded-full bg-muted"
                      aria-hidden="true"
                    >
                      <div
                        class="h-full rounded-full"
                        [class]="group.full ? 'bg-chart-5' : 'bg-chart-1'"
                        [style.width.%]="group.width"
                      ></div>
                    </div>
                  </li>
                }
              </ol>
            }
          </section>
        </div>
      </div>
    }
  `,
})
export class DashboardPage {
  private readonly api = inject(Api);
  private readonly theme = inject(ThemeService);

  protected readonly text = ui.dashboard;
  protected readonly nav = ui.nav;
  protected readonly common = ui.common;
  protected readonly skeletonCards = [1, 2, 3, 4, 5, 6, 7, 8];
  protected readonly skeletonCharts = [1, 2, 3, 4];

  readonly data = signal<DashboardDto | null>(null);
  readonly error = signal<string | null>(null);
  readonly loading = signal(false);
  private sequence = 0;

  /** Token colours, re-read whenever the light/dark theme flips. */
  readonly palette = computed(() => {
    this.theme.isDark();
    return chartTheme();
  });

  protected readonly description = computed(() => {
    const data = this.data();
    return data ? interpolate(this.text.description, { date: formatDate(data.today) }) : '';
  });

  /** No active students and no active groups: nothing meaningful to chart yet. */
  readonly isEmpty = computed(() => {
    const kpis = this.data()?.kpis;
    const data = this.data();
    return (
      !!kpis &&
      count(kpis.activeStudents) === 0 &&
      count(kpis.activeGroups) === 0 &&
      isZero(kpis.outstandingTotal) &&
      (data?.monthlyBilling ?? []).every((m) => isZero(m.collected) && isZero(m.due))
    );
  });

  readonly kpis = computed<Kpi[]>(() => {
    const data = this.data();
    if (!data) {
      return [];
    }
    const k = data.kpis;
    const t = this.text.kpi;
    const enrolled = formatNumber(count(k.activeEnrollments));
    return [
      {
        key: 'students',
        label: t.activeStudents,
        value: formatNumber(count(k.activeStudents)),
        icon: LucideGraduationCap,
        tone: 'primary',
      },
      {
        key: 'groups',
        label: t.activeGroups,
        value: formatNumber(count(k.activeGroups)),
        hint: interpolate(t.enrollmentsHint, { count: enrolled }),
        icon: LucideUsers,
        tone: 'info',
      },
      {
        key: 'capacity',
        label: t.capacity,
        value: percentText(k.capacityUtilisation),
        hint:
          ratio(k.capacityUtilisation) === null
            ? t.noData
            : interpolate(t.capacityHint, {
                enrolled,
                capacity: formatNumber(count(k.totalCapacity)),
              }),
        icon: LucideGauge,
        tone: 'warning',
      },
      {
        key: 'collected',
        label: t.collected,
        value: displayMoney(k.collectedThisMonth),
        hint: periodLabel(data.currentPeriod),
        icon: LucideHandCoins,
        tone: 'success',
      },
      {
        key: 'outstanding',
        label: t.outstanding,
        value: displayMoney(k.outstandingThisMonth),
        hint: periodLabel(data.currentPeriod),
        icon: LucideReceipt,
        tone: 'danger',
      },
      {
        key: 'outstandingTotal',
        label: t.outstandingTotal,
        value: displayMoney(k.outstandingTotal),
        hint: t.outstandingTotalHint,
        icon: LucideWallet,
        tone: 'danger',
      },
      {
        key: 'attendance',
        label: t.attendance,
        value: percentText(k.attendanceRateLast30Days),
        hint: ratio(k.attendanceRateLast30Days) === null ? t.noData : t.attendanceHint,
        icon: LucidePercent,
        tone: 'success',
      },
    ];
  });

  protected readonly today = computed(() => {
    const sessions = this.data()?.todaySessions;
    const planned = count(sessions?.planned);
    const recorded = count(sessions?.recorded);
    return {
      planned,
      value: interpolate(this.text.today.value, {
        recorded: formatNumber(recorded),
        planned: formatNumber(planned),
      }),
      percent: planned === 0 ? 0 : Math.min(100, (recorded / planned) * 100),
    };
  });

  readonly billing = computed(() => {
    const months = this.data()?.monthlyBilling ?? [];
    const t = this.text.billing;
    return {
      options: billingOption(months, this.palette()),
      summary: billingSummary(months),
      columns: [t.period, t.collected, t.due],
      rows: months.map((m) => [
        periodLabel(m.period),
        displayMoney(m.collected),
        displayMoney(m.due),
      ]),
    };
  });

  readonly attendance = computed(() => {
    const weeks = this.data()?.weeklyAttendance ?? [];
    const t = this.text.attendance;
    return {
      options: attendanceOption(weeks, this.palette()),
      summary: attendanceSummary(weeks),
      columns: [t.week, t.rate, t.counted],
      rows: weeks.map((w) => [
        weekRange(w),
        ratio(w.rate) === null ? t.noRecords : percentText(w.rate),
        formatNumber(count(w.counted)),
      ]),
    };
  });

  readonly sports = computed(() => {
    const sports = this.data()?.enrollmentsBySport ?? [];
    const t = this.text.sports;
    return {
      options: sportsOption(sports, this.palette()),
      summary: sportsSummary(sports),
      columns: [t.sport, t.enrollments],
      rows: sports.map((s) => [sportName(s), formatNumber(count(s.activeEnrollments))]),
      empty: noEnrollments(sports),
    };
  });

  readonly groups = computed(() =>
    (this.data()?.groupUtilisation ?? []).map((g) => {
      const value = ratio(g.utilisation) ?? 0;
      return {
        id: g.groupId,
        name: g.groupName,
        fill: interpolate(this.text.groups.fill, {
          enrolled: formatNumber(count(g.activeEnrollments)),
          capacity: formatNumber(count(g.capacity)),
        }),
        percent: percentText(g.utilisation),
        width: Math.min(100, value * 100),
        full: value >= 1,
      };
    }),
  );

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    const sequence = ++this.sequence;
    this.loading.set(true);
    this.error.set(null);
    try {
      const data = await this.api.invoke(apiV1DashboardGet);
      if (sequence === this.sequence) {
        this.data.set(data);
      }
    } catch (err) {
      if (sequence === this.sequence) {
        this.error.set(problemMessage(toProblem(err)));
      }
    } finally {
      if (sequence === this.sequence) {
        this.loading.set(false);
      }
    }
  }
}
