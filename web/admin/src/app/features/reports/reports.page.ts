import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  LucideDownload,
  LucideHandCoins,
  LucidePercent,
  LucideReceipt,
  LucideUsers,
  LucideWallet,
} from '@lucide/angular';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../api/api';
import {
  apiV1ReportsAttendanceGet,
  apiV1ReportsOccupancyGet,
  apiV1ReportsRevenueGet,
} from '../../api/functions';
import {
  AttendanceCellDto,
  AttendanceReportDto,
  OccupancyCellDto,
  OccupancyReportDto,
  RevenueReportDto,
} from '../../api/models';
import { problemMessage, toProblem } from '../../core/api/problem';
import { formatDate, formatNumber } from '../../core/format/format';
import { errorMessages, interpolate, ui } from '../../core/i18n/tr';
import { displayMoney } from '../../core/money/money';
import { ThemeService } from '../../core/theme/theme.service';
import { SchoolClock } from '../../core/time/school-clock';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { StatCardComponent } from '../../shared/ui/stat-card.component';
import { ToastService } from '../../shared/ui/toast.service';
import { DashboardChartComponent } from '../dashboard/dashboard-chart.component';
import { chartTheme, count, ratio } from '../dashboard/dashboard-charts';
import {
  CHART_ROWS,
  DEFAULT_MONTHS,
  Dimension,
  ReportTab,
  attendanceOption,
  attendanceSummary,
  bucketLabel,
  bucketShare,
  clampRange,
  counted,
  lastMonths,
  monthOptions,
  occupancyOption,
  occupancySummary,
  periodLabel,
  rateText,
  revenueOption,
  revenueSummary,
  rowName,
} from './report-charts';

/** A drill-down: the groups of one sport or one coach. */
interface Filter {
  kind: 'sport' | 'coach';
  id: string;
  name: string;
}

interface Request {
  tab: ReportTab;
  from: string;
  to: string;
  by: Dimension;
  filter: Filter | null;
}

const TABS: ReportTab[] = ['attendance', 'occupancy', 'revenue'];
const DIMENSIONS: Dimension[] = ['group', 'sport', 'coach'];

/**
 * P3-09 (DEC-412): the School Admin's reports. One tab at a time is loaded; the month range and
 * the grouping are shared by the tabs. Every chart has a text summary and its numbers as a table
 * (the dashboard's accessible chart card), and the full report is always a table below it.
 */
@Component({
  selector: 'app-reports-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The tabs swap colours without a transition: a half-faded tab would fail the contrast check (and helps nobody).
  imports: [
    DashboardChartComponent,
    LucideDownload,
    PageHeaderComponent,
    RouterLink,
    StatCardComponent,
  ],
  template: `
    <app-page-header [title]="nav.reports" [description]="text.description">
      <button
        type="button"
        class="btn btn-secondary"
        [disabled]="downloading()"
        [attr.aria-label]="downloadLabel()"
        (click)="download()"
        data-testid="report-download"
      >
        <svg lucideDownload class="size-4" aria-hidden="true"></svg>
        {{ downloading() ? text.downloading : text.download }}
      </button>
    </app-page-header>

    <div class="card mb-4 flex flex-col gap-4 p-4">
      <div role="tablist" class="flex flex-wrap gap-1" [attr.aria-label]="text.tabsLabel">
        @for (t of tabs; track t) {
          <button
            type="button"
            role="tab"
            [id]="'report-tab-' + t"
            [attr.aria-selected]="tab() === t"
            [attr.aria-controls]="'report-panel'"
            [attr.tabindex]="tab() === t ? 0 : -1"
            class="inline-flex h-9 items-center rounded-md px-3 text-xs font-semibold focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-card focus-visible:outline-none"
            [class]="
              tab() === t
                ? 'bg-primary text-primary-foreground'
                : 'text-muted-foreground hover:bg-muted hover:text-foreground'
            "
            (click)="selectTab(t)"
            (keydown)="tabKey($event, t)"
          >
            {{ text.tabs[t] }}
          </button>
        }
      </div>

      <div class="flex flex-wrap items-end gap-3">
        <label class="flex flex-col gap-1 text-sm">
          <span class="font-medium">{{ text.from }}</span>
          <select
            class="input h-9"
            [value]="from()"
            (change)="setFrom($any($event.target).value)"
            data-testid="report-from"
          >
            @for (m of months(); track m.value) {
              <option [value]="m.value" [selected]="m.value === from()">{{ m.label }}</option>
            }
          </select>
        </label>
        <label class="flex flex-col gap-1 text-sm">
          <span class="font-medium">{{ text.to }}</span>
          <select
            class="input h-9"
            [value]="to()"
            (change)="setTo($any($event.target).value)"
            data-testid="report-to"
          >
            @for (m of months(); track m.value) {
              <option [value]="m.value" [selected]="m.value === to()">{{ m.label }}</option>
            }
          </select>
        </label>
        <div role="group" class="flex gap-1" [attr.aria-label]="text.presets">
          @for (n of presets; track n) {
            <button
              type="button"
              class="btn btn-ghost btn-sm"
              [attr.aria-pressed]="isPreset(n)"
              (click)="setLast(n)"
            >
              {{ presetLabel(n) }}
            </button>
          }
        </div>
        @if (tab() !== 'revenue') {
          <label class="flex flex-col gap-1 text-sm">
            <span class="font-medium">{{ text.by }}</span>
            <select
              class="input h-9"
              [value]="by()"
              (change)="setBy($any($event.target).value)"
              data-testid="report-by"
            >
              @for (d of dimensions; track d) {
                <option [value]="d" [selected]="d === by()">{{ text.dimensions[d] }}</option>
              }
            </select>
          </label>
        }
      </div>

      @if (filter() && tab() !== 'revenue') {
        <div class="flex flex-wrap items-center gap-2 text-sm" data-testid="report-filter">
          <span class="badge">{{ filterLabel() }}</span>
          <button type="button" class="btn btn-ghost btn-sm" (click)="clearFilter()">
            {{ text.clearFilter }}
          </button>
        </div>
      }
    </div>

    <div
      id="report-panel"
      role="tabpanel"
      [attr.aria-labelledby]="'report-tab-' + tab()"
      [attr.aria-busy]="loading()"
    >
      @if (error(); as message) {
        <div role="alert" class="card flex flex-col items-start gap-3 p-4 text-sm text-destructive">
          <span>{{ message }}</span>
          <button type="button" class="btn btn-secondary" (click)="reload()">
            {{ common.retry }}
          </button>
        </div>
      } @else if (loading() && !hasData()) {
        <div aria-busy="true" data-testid="report-skeleton">
          <p role="status" class="sr-only">{{ text.loading }}</p>
          <div class="h-80 animate-pulse rounded-xl border border-border bg-muted/60"></div>
        </div>
      } @else {
        <div [class.opacity-60]="loading()">
          @switch (tab()) {
            @case ('attendance') {
              @if (attendance(); as report) {
                @if (report.rows.length === 0) {
                  <p class="card p-6 text-center text-sm text-muted-foreground">
                    {{ text.attendance.empty }}
                  </p>
                } @else {
                  <app-dashboard-chart
                    key="report-attendance"
                    [heading]="text.attendance.title"
                    [subtitle]="text.attendance.subtitle"
                    [summary]="attendanceView().summary"
                    [options]="attendanceView().options"
                    [columns]="attendanceView().columns"
                    [rows]="attendanceView().chartRows"
                    [height]="300"
                  />
                  @if (report.rows.length > chartRowLimit) {
                    <p class="mt-2 text-xs text-muted-foreground">{{ chartLimitText() }}</p>
                  }
                  <section class="card mt-4 min-w-0" aria-labelledby="attendance-table-title">
                    <h2 id="attendance-table-title" class="px-4 pt-4 text-sm font-semibold">
                      {{ text.attendance.tableTitle }}
                    </h2>
                    <div class="overflow-x-auto p-4">
                      <table class="w-full text-sm" data-testid="attendance-table">
                        <thead class="border-b border-border bg-muted/60">
                          <tr>
                            <th scope="col" class="h-9 px-3 text-left text-xs font-semibold">
                              {{ text.dimensions[byKey(report.by)] }}
                            </th>
                            @for (p of report.periods; track p) {
                              <th scope="col" class="h-9 px-3 text-right text-xs font-semibold">
                                {{ label(p) }}
                              </th>
                            }
                            <th scope="col" class="h-9 px-3 text-right text-xs font-semibold">
                              {{ text.total }}
                            </th>
                          </tr>
                        </thead>
                        <tbody>
                          @for (row of report.rows; track row.id ?? '-') {
                            <tr class="border-b border-border/60">
                              <th scope="row" class="px-3 py-2 text-left font-medium">
                                <span>{{ name(row, report.by) }}</span>
                                @if (canDrill(row.id, report.by)) {
                                  <button
                                    type="button"
                                    class="ml-2 text-xs font-semibold text-primary hover:underline"
                                    [attr.aria-label]="drillLabel(row, report.by)"
                                    (click)="drill(row, report.by)"
                                  >
                                    {{ text.drill }}
                                  </button>
                                }
                              </th>
                              @for (cell of row.months; track $index) {
                                <td class="px-3 py-2 text-right font-mono">
                                  {{ rate(cell.rate) }}
                                  <span class="block text-[11px] text-muted-foreground">{{
                                    countedText(cell)
                                  }}</span>
                                </td>
                              }
                              <td class="px-3 py-2 text-right font-mono font-semibold">
                                {{ rate(row.total.rate) }}
                                <span class="block text-[11px] text-muted-foreground">{{
                                  countedText(row.total)
                                }}</span>
                              </td>
                            </tr>
                          }
                        </tbody>
                        <tfoot class="border-t-2 border-border">
                          <tr>
                            <th scope="row" class="px-3 py-2 text-left font-semibold">
                              {{ text.schoolTotal }}
                            </th>
                            @for (cell of report.total.months; track $index) {
                              <td class="px-3 py-2 text-right font-mono font-semibold">
                                {{ rate(cell.rate) }}
                              </td>
                            }
                            <td class="px-3 py-2 text-right font-mono font-semibold">
                              {{ rate(report.total.total.rate) }}
                            </td>
                          </tr>
                        </tfoot>
                      </table>
                    </div>
                  </section>
                }
              }
            }
            @case ('occupancy') {
              @if (occupancy(); as report) {
                @if (report.rows.length === 0) {
                  <p class="card p-6 text-center text-sm text-muted-foreground">
                    {{ text.occupancy.empty }}
                  </p>
                } @else {
                  <app-dashboard-chart
                    key="report-occupancy"
                    [heading]="text.occupancy.title"
                    [subtitle]="text.occupancy.subtitle"
                    [summary]="occupancyView().summary"
                    [options]="occupancyView().options"
                    [columns]="occupancyView().columns"
                    [rows]="occupancyView().chartRows"
                    [height]="280"
                  />
                  <section class="card mt-4 min-w-0" aria-labelledby="occupancy-table-title">
                    <h2 id="occupancy-table-title" class="px-4 pt-4 text-sm font-semibold">
                      {{ text.occupancy.tableTitle }}
                    </h2>
                    <p class="px-4 text-xs text-muted-foreground">{{ text.occupancy.note }}</p>
                    <div class="overflow-x-auto p-4">
                      <table class="w-full text-sm" data-testid="occupancy-table">
                        <thead class="border-b border-border bg-muted/60">
                          <tr>
                            <th scope="col" class="h-9 px-3 text-left text-xs font-semibold">
                              {{ text.dimensions[byKey(report.by)] }}
                            </th>
                            @for (p of report.periods; track p.period) {
                              <th scope="col" class="h-9 px-3 text-right text-xs font-semibold">
                                {{ label(p.period) }}
                                <span class="block font-normal text-muted-foreground">{{
                                  date(p.referenceDate)
                                }}</span>
                              </th>
                            }
                          </tr>
                        </thead>
                        <tbody>
                          @for (row of report.rows; track row.id ?? '-') {
                            <tr class="border-b border-border/60">
                              <th scope="row" class="px-3 py-2 text-left font-medium">
                                <span>{{ name(row, report.by) }}</span>
                                @if (canDrill(row.id, report.by)) {
                                  <button
                                    type="button"
                                    class="ml-2 text-xs font-semibold text-primary hover:underline"
                                    [attr.aria-label]="drillLabel(row, report.by)"
                                    (click)="drill(row, report.by)"
                                  >
                                    {{ text.drill }}
                                  </button>
                                }
                              </th>
                              @for (cell of row.months; track $index) {
                                <td class="px-3 py-2 text-right font-mono">
                                  {{ fill(cell) }}
                                </td>
                              }
                            </tr>
                          }
                        </tbody>
                        <tfoot class="border-t-2 border-border">
                          <tr>
                            <th scope="row" class="px-3 py-2 text-left font-semibold">
                              {{ text.schoolTotal }}
                            </th>
                            @for (cell of report.total.months; track $index) {
                              <td class="px-3 py-2 text-right font-mono font-semibold">
                                {{ fill(cell) }}
                              </td>
                            }
                          </tr>
                        </tfoot>
                      </table>
                    </div>
                  </section>
                }
              }
            }
            @case ('revenue') {
              @if (revenue(); as report) {
                <ul
                  class="mb-4 grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4"
                  [attr.aria-label]="text.revenue.title"
                >
                  @for (kpi of revenueKpis(); track kpi.key) {
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
                </ul>
                <app-dashboard-chart
                  key="report-revenue"
                  [heading]="text.revenue.title"
                  [subtitle]="text.revenue.subtitle"
                  [summary]="revenueView().summary"
                  [options]="revenueView().options"
                  [columns]="revenueView().columns"
                  [rows]="revenueView().rows"
                  [height]="300"
                />
                <section class="card mt-4 min-w-0" aria-labelledby="revenue-table-title">
                  <h2 id="revenue-table-title" class="px-4 pt-4 text-sm font-semibold">
                    {{ text.revenue.tableTitle }}
                  </h2>
                  <div class="overflow-x-auto p-4">
                    <table class="w-full text-sm" data-testid="revenue-table">
                      <thead class="border-b border-border bg-muted/60">
                        <tr>
                          @for (c of revenueColumns(); track $index) {
                            <th
                              scope="col"
                              class="h-9 px-3 text-xs font-semibold"
                              [class.text-left]="$first"
                              [class.text-right]="!$first"
                            >
                              {{ c }}
                            </th>
                          }
                        </tr>
                      </thead>
                      <tbody>
                        @for (row of revenueView().table; track $index) {
                          <tr class="border-b border-border/60">
                            @for (cell of row; track $index) {
                              @if ($first) {
                                <th scope="row" class="px-3 py-2 text-left font-medium">
                                  {{ cell }}
                                </th>
                              } @else {
                                <td class="px-3 py-2 text-right font-mono">{{ cell }}</td>
                              }
                            }
                          </tr>
                        }
                      </tbody>
                      <tfoot class="border-t-2 border-border">
                        <tr>
                          @for (cell of revenueView().totalRow; track $index) {
                            @if ($first) {
                              <th scope="row" class="px-3 py-2 text-left font-semibold">
                                {{ cell }}
                              </th>
                            } @else {
                              <td class="px-3 py-2 text-right font-mono font-semibold">
                                {{ cell }}
                              </td>
                            }
                          }
                        </tr>
                      </tfoot>
                    </table>
                  </div>
                </section>

                <section class="card mt-4 p-4" aria-labelledby="overdue-title">
                  <h2 id="overdue-title" class="text-sm font-semibold">
                    {{ text.revenue.overdue.title }}
                  </h2>
                  <p class="text-xs text-muted-foreground">{{ overdueSubtitle() }}</p>
                  <p class="mt-2 text-sm">{{ overdueSummary() }}</p>
                  <table class="mt-3 w-full text-sm" data-testid="overdue-table">
                    <thead class="border-b border-border bg-muted/60">
                      <tr>
                        <th scope="col" class="h-9 px-3 text-left text-xs font-semibold">
                          {{ text.revenue.overdue.bucket }}
                        </th>
                        <th scope="col" class="h-9 px-3 text-right text-xs font-semibold">
                          {{ text.revenue.overdue.dues }}
                        </th>
                        <th scope="col" class="h-9 px-3 text-right text-xs font-semibold">
                          {{ text.revenue.overdue.amount }}
                        </th>
                        <th scope="col" class="w-1/3 px-3"><span class="sr-only">%</span></th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (bucket of report.overdue; track $index) {
                        <tr class="border-b border-border/60">
                          <th scope="row" class="px-3 py-2 text-left font-medium">
                            {{ bucketName(bucket) }}
                          </th>
                          <td class="px-3 py-2 text-right font-mono">
                            {{ number(bucket.dues) }}
                          </td>
                          <td class="px-3 py-2 text-right font-mono">
                            {{ money(bucket.amount) }}
                          </td>
                          <td class="px-3 py-2" aria-hidden="true">
                            <div class="h-2 overflow-hidden rounded-full bg-muted">
                              <div
                                class="h-full rounded-full bg-chart-5"
                                [style.width.%]="share(bucket.amount, report.overdueAmount)"
                              ></div>
                            </div>
                          </td>
                        </tr>
                      }
                    </tbody>
                    <tfoot class="border-t-2 border-border">
                      <tr>
                        <th scope="row" class="px-3 py-2 text-left font-semibold">
                          {{ text.revenue.overdue.total }}
                        </th>
                        <td class="px-3 py-2 text-right font-mono font-semibold">
                          {{ number(report.overdueDues) }}
                        </td>
                        <td class="px-3 py-2 text-right font-mono font-semibold">
                          {{ money(report.overdueAmount) }}
                        </td>
                        <td></td>
                      </tr>
                      <tr>
                        <th
                          scope="row"
                          class="px-3 py-2 text-left font-medium text-muted-foreground"
                        >
                          {{ text.revenue.overdue.notYetDue }}
                        </th>
                        <td class="px-3 py-2 text-right font-mono text-muted-foreground">
                          {{ number(report.notYetDueDues) }}
                        </td>
                        <td class="px-3 py-2 text-right font-mono text-muted-foreground">
                          {{ money(report.notYetDueAmount) }}
                        </td>
                        <td></td>
                      </tr>
                    </tfoot>
                  </table>
                  <a
                    routerLink="/dues"
                    class="mt-3 inline-block text-sm font-semibold text-primary hover:underline"
                  >
                    {{ text.revenue.overdue.link }}
                  </a>
                </section>
              }
            }
          }
        </div>
      }
    </div>
  `,
})
export class ReportsPage {
  private readonly api = inject(Api);
  private readonly http = inject(HttpClient);
  private readonly theme = inject(ThemeService);
  private readonly clock = inject(SchoolClock);
  private readonly toast = inject(ToastService);

  protected readonly text = ui.reports;
  protected readonly nav = ui.nav;
  protected readonly common = ui.common;
  protected readonly tabs = TABS;
  protected readonly dimensions = DIMENSIONS;
  protected readonly presets = [3, 6, 12];
  protected readonly chartRowLimit = CHART_ROWS;
  protected readonly chartLimitText = computed(() =>
    interpolate(this.text.chartLimit, { count: CHART_ROWS }),
  );
  protected readonly revenueColumns = computed(() => [
    ui.reports.revenue.period,
    ui.reports.revenue.listFees,
    ui.reports.revenue.siblingDiscount,
    ui.reports.revenue.groupChangeWaived,
    ui.reports.revenue.accrued,
    ui.reports.revenue.collectedForPeriod,
    ui.reports.revenue.outstanding,
    ui.reports.revenue.collectedInMonth,
    ui.reports.revenue.collectionRate,
  ]);

  /** The school's current month; the server stays authoritative and refuses later months. */
  readonly currentPeriod = computed(() => this.clock.today().slice(0, 7));
  readonly months = computed(() => monthOptions(this.currentPeriod()));

  readonly tab = signal<ReportTab>('attendance');
  readonly from = signal(lastMonths(this.currentPeriod(), DEFAULT_MONTHS).from);
  readonly to = signal(this.currentPeriod());
  readonly by = signal<Dimension>('group');
  readonly filter = signal<Filter | null>(null);

  readonly attendance = signal<AttendanceReportDto | null>(null);
  readonly occupancy = signal<OccupancyReportDto | null>(null);
  readonly revenue = signal<RevenueReportDto | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly downloading = signal(false);
  private sequence = 0;

  readonly request = computed<Request>(() => ({
    tab: this.tab(),
    from: this.from(),
    to: this.to(),
    by: this.by(),
    filter: this.filter(),
  }));

  /** Token colours, re-read whenever the light/dark theme flips. */
  readonly palette = computed(() => {
    this.theme.isDark();
    return chartTheme();
  });

  readonly hasData = computed(() => {
    switch (this.tab()) {
      case 'attendance':
        return this.attendance() !== null;
      case 'occupancy':
        return this.occupancy() !== null;
      default:
        return this.revenue() !== null;
    }
  });

  protected readonly downloadLabel = computed(() =>
    interpolate(this.text.downloadLabel, { report: this.text.tabs[this.tab()] }),
  );

  protected readonly filterLabel = computed(() => {
    const f = this.filter();
    if (!f) {
      return '';
    }
    return interpolate(f.kind === 'sport' ? this.text.filteredBySport : this.text.filteredByCoach, {
      name: f.name,
    });
  });

  readonly attendanceView = computed(() => {
    const report = this.attendance();
    if (!report) {
      return { options: {}, summary: '', columns: [], chartRows: [] as string[][] };
    }
    const t = this.text;
    return {
      options: attendanceOption(report, this.palette()),
      summary: attendanceSummary(report),
      columns: [ui.dashboard.billing.period, t.schoolTotal],
      chartRows: report.total.months.map((m) => [periodLabel(m.period ?? ''), rateText(m.rate)]),
    };
  });

  readonly occupancyView = computed(() => {
    const report = this.occupancy();
    if (!report) {
      return { options: {}, summary: '', columns: [], chartRows: [] as string[][] };
    }
    const t = this.text.occupancy;
    return {
      options: occupancyOption(report, this.palette()),
      summary: occupancySummary(report),
      columns: [ui.dashboard.billing.period, t.enrolled, t.capacity, t.utilisation],
      chartRows: report.total.months.map((m) => [
        periodLabel(m.period),
        formatNumber(count(m.enrolled)),
        formatNumber(count(m.capacity)),
        rateText(m.utilisation),
      ]),
    };
  });

  readonly revenueView = computed(() => {
    const report = this.revenue();
    const t = this.text.revenue;
    if (!report) {
      return { options: {}, summary: '', columns: [], rows: [], table: [], totalRow: [] };
    }
    const line = (p: RevenueReportDto['total'], name: string) => [
      name,
      displayMoney(p.listFees),
      displayMoney(p.siblingDiscount),
      displayMoney(p.groupChangeWaived),
      displayMoney(p.accrued),
      displayMoney(p.collectedForPeriod),
      displayMoney(p.outstanding),
      displayMoney(p.collectedInMonth),
      rateText(p.collectionRate),
    ];
    return {
      options: revenueOption(report, this.palette()),
      summary: revenueSummary(report),
      columns: [t.period, t.accrued, t.collectedForPeriod, t.collectedInMonth],
      rows: report.periods.map((p) => [
        periodLabel(p.period ?? ''),
        displayMoney(p.accrued),
        displayMoney(p.collectedForPeriod),
        displayMoney(p.collectedInMonth),
      ]),
      table: report.periods.map((p) => line(p, periodLabel(p.period ?? ''))),
      totalRow: line(report.total, this.text.total),
    };
  });

  readonly revenueKpis = computed(() => {
    const report = this.revenue();
    if (!report) {
      return [];
    }
    const t = this.text.revenue;
    const range = interpolate(t.kpiRangeHint, {
      from: periodLabel(report.from),
      to: periodLabel(report.to),
    });
    return [
      {
        key: 'accrued',
        label: t.accrued,
        value: displayMoney(report.total.accrued),
        hint: range,
        icon: LucideReceipt,
        tone: 'info' as const,
      },
      {
        key: 'collected',
        label: t.collectedForPeriod,
        value: displayMoney(report.total.collectedForPeriod),
        hint: `${t.collectionRate}: ${rateText(report.total.collectionRate)}`,
        icon: LucideHandCoins,
        tone: 'success' as const,
      },
      {
        key: 'outstanding',
        label: t.outstanding,
        value: displayMoney(report.total.outstanding),
        hint: range,
        icon: LucideWallet,
        tone: 'danger' as const,
      },
      {
        key: 'sibling',
        label: t.siblingDiscount,
        value: displayMoney(report.total.siblingDiscount),
        hint: interpolate(t.siblingHint, {
          count: formatNumber(count(report.total.siblingDiscountedCharges)),
        }),
        icon: count(report.total.siblingDiscountedCharges) > 0 ? LucideUsers : LucidePercent,
        tone: 'warning' as const,
      },
    ];
  });

  protected readonly overdueSubtitle = computed(() => {
    const report = this.revenue();
    return report
      ? interpolate(this.text.revenue.overdue.subtitle, {
          date: formatDate(report.today),
          day: count(report.dueDay),
        })
      : '';
  });

  protected readonly overdueSummary = computed(() => {
    const report = this.revenue();
    return report
      ? interpolate(this.text.revenue.overdue.summary, {
          count: formatNumber(count(report.overdueDues)),
          amount: displayMoney(report.overdueAmount),
        })
      : '';
  });

  constructor() {
    effect(() => {
      const request = this.request();
      untracked(() => void this.load(request));
    });
  }

  selectTab(tab: ReportTab): void {
    this.tab.set(tab);
  }

  /** Arrow keys move between the tabs (WAI-ARIA tabs pattern, automatic activation). */
  tabKey(event: KeyboardEvent, tab: ReportTab): void {
    const index = TABS.indexOf(tab);
    const next =
      event.key === 'ArrowRight'
        ? TABS[(index + 1) % TABS.length]
        : event.key === 'ArrowLeft'
          ? TABS[(index + TABS.length - 1) % TABS.length]
          : event.key === 'Home'
            ? TABS[0]
            : event.key === 'End'
              ? TABS[TABS.length - 1]
              : null;
    if (next) {
      event.preventDefault();
      this.tab.set(next);
      (event.currentTarget as HTMLElement | null)?.parentElement
        ?.querySelector<HTMLElement>(`#report-tab-${next}`)
        ?.focus();
    }
  }

  setFrom(value: string): void {
    this.applyRange(clampRange(value, this.to(), this.currentPeriod(), 'from'));
  }

  setTo(value: string): void {
    this.applyRange(clampRange(this.from(), value, this.currentPeriod(), 'to'));
  }

  setLast(months: number): void {
    this.applyRange(lastMonths(this.currentPeriod(), months));
  }

  isPreset(months: number): boolean {
    const range = lastMonths(this.currentPeriod(), months);
    return range.from === this.from() && range.to === this.to();
  }

  presetLabel(months: number): string {
    return interpolate(this.text.lastMonths, { count: months });
  }

  setBy(value: string): void {
    if ((DIMENSIONS as string[]).includes(value)) {
      this.by.set(value as Dimension);
      this.filter.set(null);
    }
  }

  canDrill(id: string | null | undefined, by: string): boolean {
    return !!id && by !== 'Group';
  }

  drill(row: { id?: string | null; name: string }, by: string): void {
    if (!row.id) {
      return;
    }
    this.filter.set({
      kind: by === 'Sport' ? 'sport' : 'coach',
      id: row.id,
      name: rowName(row, by),
    });
    this.by.set('group');
  }

  drillLabel(row: { id?: string | null; name: string }, by: string): string {
    return interpolate(this.text.drillLabel, { name: rowName(row, by) });
  }

  clearFilter(): void {
    this.filter.set(null);
  }

  reload(): void {
    void this.load(this.request());
  }

  async download(): Promise<void> {
    const request = this.request();
    this.downloading.set(true);
    try {
      const response = await firstValueFrom(
        this.http.get(`${this.api.rootUrl}/api/v1/reports/${request.tab}/csv`, {
          params: this.params(request),
          responseType: 'blob',
          observe: 'response',
        }),
      );
      const name =
        fileName(response.headers.get('Content-Disposition')) ??
        `${request.tab}_${request.from}_${request.to}.csv`;
      saveFile(response.body ?? new Blob([]), name);
    } catch (err) {
      this.toast.error(
        err instanceof HttpErrorResponse && err.status === 429
          ? errorMessages['Request.TooManyRequests']
          : this.text.downloadFailed,
      );
    } finally {
      this.downloading.set(false);
    }
  }

  protected byKey(by: string): Dimension {
    return by.toLowerCase() as Dimension;
  }

  protected name(row: { id?: string | null; name: string }, by: string): string {
    return rowName(row, by);
  }

  protected label(period: string): string {
    return periodLabel(period);
  }

  protected date(value: string): string {
    return formatDate(value);
  }

  protected rate(value: number | string | null | undefined): string {
    return rateText(value);
  }

  protected countedText(cell: AttendanceCellDto): string {
    return interpolate(this.text.attendance.counted, { count: formatNumber(counted(cell)) });
  }

  /** "3/4 · %75", or "—" when no group was open that month. */
  protected fill(cell: OccupancyCellDto): string {
    const capacity = count(cell.capacity);
    if (capacity === 0 && count(cell.enrolled) === 0) {
      return this.text.noValue;
    }
    const fill = interpolate(this.text.occupancy.fill, {
      enrolled: formatNumber(count(cell.enrolled)),
      capacity: formatNumber(capacity),
    });
    return ratio(cell.utilisation) === null ? fill : `${fill} · ${rateText(cell.utilisation)}`;
  }

  protected bucketName(bucket: RevenueReportDto['overdue'][number]): string {
    return bucketLabel(bucket);
  }

  protected number(value: number | string): string {
    return formatNumber(count(value));
  }

  protected money(value: RevenueReportDto['overdueAmount']): string {
    return displayMoney(value);
  }

  protected share(
    amount: RevenueReportDto['overdueAmount'],
    total: RevenueReportDto['overdueAmount'],
  ): number {
    return bucketShare(amount, total);
  }

  private params(request: Request): HttpParams {
    let params = new HttpParams().set('from', request.from).set('to', request.to);
    if (request.tab !== 'revenue') {
      params = params.set('by', request.by);
      if (request.filter) {
        params = params.set(
          request.filter.kind === 'sport' ? 'sportId' : 'coachId',
          request.filter.id,
        );
      }
    }
    return params;
  }

  private async load(request: Request): Promise<void> {
    const sequence = ++this.sequence;
    this.loading.set(true);
    this.error.set(null);
    const base = { from: request.from, to: request.to };
    const filter = {
      by: request.by,
      sportId: request.filter?.kind === 'sport' ? request.filter.id : undefined,
      coachId: request.filter?.kind === 'coach' ? request.filter.id : undefined,
    };
    try {
      switch (request.tab) {
        case 'attendance': {
          const data = await this.api.invoke(apiV1ReportsAttendanceGet, { ...base, ...filter });
          if (sequence === this.sequence) this.attendance.set(data);
          break;
        }
        case 'occupancy': {
          const data = await this.api.invoke(apiV1ReportsOccupancyGet, { ...base, ...filter });
          if (sequence === this.sequence) this.occupancy.set(data);
          break;
        }
        default: {
          const data = await this.api.invoke(apiV1ReportsRevenueGet, base);
          if (sequence === this.sequence) this.revenue.set(data);
        }
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

  private applyRange(range: { from: string; to: string }): void {
    this.from.set(range.from);
    this.to.set(range.to);
  }
}

/** The file name from <c>Content-Disposition: attachment; filename=…</c>, if present and plain. */
export function fileName(header: string | null): string | null {
  const match = /filename="?([A-Za-z0-9._-]+)"?/.exec(header ?? '');
  return match ? match[1] : null;
}

/** Hands a downloaded file to the browser without navigating away. */
export function saveFile(blob: Blob, name: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.rel = 'noopener';
  document.body.appendChild(link);
  link.click();
  link.remove();
  // Some browsers cancel a download whose URL is revoked at once; a short delay is enough.
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
