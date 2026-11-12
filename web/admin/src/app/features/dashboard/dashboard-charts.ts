import type { EChartsCoreOption } from 'echarts/core';

import {
  MonthlyBillingDto,
  MoneyDto,
  SportEnrollmentsDto,
  WeeklyAttendanceDto,
} from '../../api/models';
import { formatDate, formatNumber, formatPercent } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { perLocale } from '../../core/i18n/locale';
import { displayMoney } from '../../core/money/money';
import { themeColor } from '../../shared/ui/chart.component';

/**
 * Pure option builders for the dashboard charts (docs/ui-tema.md, DEC-112). Colours come in as a
 * {@link ChartTheme} read from the --chart-1..5 tokens, so callers rebuild options when the theme
 * flips. ECharts needs numbers to plot; money is converted only for the series values, while every
 * label and tooltip is formatted from the original amount text.
 */
export interface ChartTheme {
  series: string[];
  text: string;
}

export const CHART_TOKENS = ['chart-1', 'chart-2', 'chart-3', 'chart-4', 'chart-5'];

export function chartTheme(): ChartTheme {
  return { series: CHART_TOKENS.map(themeColor), text: themeColor('muted-foreground') };
}

/** The API sends ints and ratios as number or numeric text; null ratios mean "nothing to divide". */
export function ratio(value: number | string | null | undefined): number | null {
  if (value === null || value === undefined || value === '') {
    return null;
  }
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

/** "0", "0.00", "-0.00": nothing billed or collected. */
export function isZero(amount: MoneyDto): boolean {
  return /^-?0+(\.0+)?$/.test(amount.amount);
}

export function count(value: number | string | null | undefined): number {
  return ratio(value) ?? 0;
}

export function percentText(value: number | string | null | undefined): string {
  const r = ratio(value);
  return r === null ? ui.dashboard.noValue : percent(r);
}

const percentFormatter = perLocale(
  (l) =>
    new Intl.NumberFormat(l, {
      style: 'percent',
      maximumFractionDigits: 1,
    }),
);

/**
 * One fraction digit, and never a misleading extreme: a ratio strictly between 0 and 1 is kept
 * within %0,1–%99,9, so 0.9996 is not "%100" and 0.0004 is not "%0".
 */
export function percent(r: number): string {
  return percentFormatter().format(r > 0 && r < 1 ? Math.min(Math.max(r, 0.001), 0.999) : r);
}

/** Sport names are required by the API; a blank one still gets a visible label. */
export function sportName(sport: SportEnrollmentsDto): string {
  return sport.sportName.trim() || ui.dashboard.noValue;
}

/** True when every sport has zero active enrollments (or there are none). */
export function noEnrollments(sports: SportEnrollmentsDto[]): boolean {
  return sports.every((s) => count(s.activeEnrollments) === 0);
}

const periodFormatter = perLocale(
  (l) => new Intl.DateTimeFormat(l, { month: 'short', year: 'numeric' }),
);
/** "2026-11" → "Kas 2026" (Turkish) or "Nov 2026" (English). */
export function periodLabel(period: string): string {
  const match = /^(\d{4})-(\d{2})$/.exec(period);
  return match
    ? periodFormatter().format(new Date(Number(match[1]), Number(match[2]) - 1, 1))
    : period;
}

/** Short axis label for a week: its Monday as "dd.MM". */
export function weekLabel(week: WeeklyAttendanceDto): string {
  return formatDate(week.weekStart).slice(0, 5);
}

export function weekRange(week: WeeklyAttendanceDto): string {
  return `${formatDate(week.weekStart)} – ${formatDate(week.weekEnd)}`;
}

/** Tooltips are HTML; names come from users. */
export function escapeHtml(value: string): string {
  return value.replace(
    /[&<>"']/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!,
  );
}

/** Plot value only; never shown to the user. */
function plotAmount(amount: MoneyDto): number {
  return Number(amount.amount);
}

function dataIndex(params: unknown): number {
  const first = (Array.isArray(params) ? params[0] : params) as { dataIndex?: number } | undefined;
  return first?.dataIndex ?? 0;
}

function legend(theme: ChartTheme): Record<string, unknown> {
  return {
    bottom: 0,
    icon: 'roundRect',
    itemWidth: 10,
    itemHeight: 10,
    textStyle: { color: theme.text },
  };
}

/** Collected (bars) vs due (line) per month: two shapes, so colour is not the only cue. */
export function billingOption(months: MonthlyBillingDto[], theme: ChartTheme): EChartsCoreOption {
  const text = ui.dashboard.billing;
  return {
    legend: legend(theme),
    grid: { bottom: 36 },
    tooltip: {
      trigger: 'axis',
      formatter: (params: unknown) => {
        const month = months[dataIndex(params)];
        return month
          ? `${escapeHtml(periodLabel(month.period))}<br/>${text.collected}: ${displayMoney(month.collected)}<br/>${text.due}: ${displayMoney(month.due)}`
          : '';
      },
    },
    xAxis: { type: 'category', data: months.map((m) => periodLabel(m.period)) },
    yAxis: {
      type: 'value',
      axisLabel: { formatter: (value: number) => `₺${formatNumber(value)}` },
    },
    series: [
      {
        name: text.collected,
        type: 'bar',
        barMaxWidth: 28,
        data: months.map((m) => plotAmount(m.collected)),
        itemStyle: { color: theme.series[0], borderRadius: [4, 4, 0, 0] },
      },
      {
        name: text.due,
        type: 'line',
        symbol: 'circle',
        symbolSize: 7,
        data: months.map((m) => plotAmount(m.due)),
        itemStyle: { color: theme.series[1] },
        lineStyle: { color: theme.series[1], width: 2 },
      },
    ],
  };
}

/** Weekly attendance rate in percent; weeks without counted records are gaps, not zeros. */
export function attendanceOption(
  weeks: WeeklyAttendanceDto[],
  theme: ChartTheme,
): EChartsCoreOption {
  const text = ui.dashboard.attendance;
  const color = theme.series[2];
  return {
    tooltip: {
      trigger: 'axis',
      formatter: (params: unknown) => {
        const week = weeks[dataIndex(params)];
        if (!week) {
          return '';
        }
        const rate = ratio(week.rate);
        return `${weekRange(week)}<br/>${text.rate}: ${rate === null ? text.noRecords : percent(rate)}<br/>${text.counted}: ${formatNumber(count(week.counted))}`;
      },
    },
    xAxis: { type: 'category', boundaryGap: false, data: weeks.map(weekLabel) },
    yAxis: {
      type: 'value',
      min: 0,
      max: 100,
      axisLabel: { formatter: (v: number) => formatPercent(v / 100) },
    },
    series: [
      {
        name: text.rate,
        type: 'line',
        smooth: true,
        symbol: 'circle',
        symbolSize: 7,
        connectNulls: false,
        data: weeks.map((w) => {
          const rate = ratio(w.rate);
          return rate === null ? null : Math.round(rate * 1000) / 10;
        }),
        itemStyle: { color },
        lineStyle: { color, width: 2 },
        areaStyle: { color, opacity: 0.12 },
      },
    ],
  };
}

/** Active enrollments per sport as a donut; colours cycle through the five tokens. */
export function sportsOption(sports: SportEnrollmentsDto[], theme: ChartTheme): EChartsCoreOption {
  return {
    legend: { ...legend(theme), type: 'scroll' },
    tooltip: {
      trigger: 'item',
      formatter: (params: unknown) => {
        const sport = sports[dataIndex(params)];
        return sport
          ? `${escapeHtml(sportName(sport))}: ${formatNumber(count(sport.activeEnrollments))}`
          : '';
      },
    },
    series: [
      {
        name: ui.dashboard.sports.title,
        type: 'pie',
        radius: ['48%', '72%'],
        center: ['50%', '44%'],
        avoidLabelOverlap: true,
        label: { show: false },
        itemStyle: { borderColor: 'transparent', borderWidth: 2, borderRadius: 4 },
        data: sports.map((s, i) => ({
          name: sportName(s),
          value: count(s.activeEnrollments),
          itemStyle: { color: theme.series[i % theme.series.length] },
        })),
      },
    ],
  };
}

export function billingSummary(months: MonthlyBillingDto[]): string {
  const last = months.at(-1);
  if (!last) {
    return ui.dashboard.billing.title;
  }
  return interpolate(ui.dashboard.billing.summary, {
    months: months.length,
    period: periodLabel(last.period),
    collected: displayMoney(last.collected),
    due: displayMoney(last.due),
  });
}

export function attendanceSummary(weeks: WeeklyAttendanceDto[]): string {
  const last = weeks.at(-1);
  const rate = last ? ratio(last.rate) : null;
  return interpolate(ui.dashboard.attendance.summary, {
    weeks: weeks.length,
    rate: rate === null ? ui.dashboard.attendance.noRecords : percent(rate),
  });
}

export function sportsSummary(sports: SportEnrollmentsDto[]): string {
  return interpolate(ui.dashboard.sports.summary, {
    list: sports
      .map((s) => `${sportName(s)} ${formatNumber(count(s.activeEnrollments))}`)
      .join(', '),
  });
}
