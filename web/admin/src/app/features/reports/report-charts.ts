import type { EChartsCoreOption } from 'echarts/core';

import {
  AttendanceCellDto,
  AttendanceReportDto,
  AttendanceRowDto,
  MoneyDto,
  OccupancyReportDto,
  RevenueReportDto,
} from '../../api/models';
import { formatNumber } from '../../core/format/format';
import { interpolate, ui } from '../../core/i18n/tr';
import { displayMoney } from '../../core/money/money';
import {
  ChartTheme,
  count,
  escapeHtml,
  percent,
  percentText,
  periodLabel,
  ratio,
} from '../dashboard/dashboard-charts';

/**
 * Pure helpers for the reports page (P3-09, DEC-412): month choices, row names and the ECharts
 * options. Same rules as the dashboard (DEC-112): colours from the theme tokens, two shapes where
 * two series are compared, money plotted as numbers but always labelled from the original text.
 */

export type ReportTab = 'attendance' | 'occupancy' | 'revenue';
export type Dimension = 'group' | 'sport' | 'coach';

/** The longest range a report covers and how far back it may start (DEC-406). */
export const MAX_MONTHS = 12;
export const HORIZON_MONTHS = 24;
export const DEFAULT_MONTHS = 6;
/** Series drawn next to the school line in the attendance chart; the table has every row. */
export const CHART_ROWS = 5;

/** "2027-03" moved by <paramref name="months"/> months. */
export function shiftPeriod(period: string, months: number): string {
  const [year, month] = period.split('-').map(Number);
  const index = year * 12 + (month - 1) + months;
  return `${Math.floor(index / 12)}-${String((index % 12) + 1).padStart(2, '0')}`;
}

/** Months between two periods, both counted ("2027-01".."2027-03" → 3). */
export function monthsBetween(from: string, to: string): number {
  const [fy, fm] = from.split('-').map(Number);
  const [ty, tm] = to.split('-').map(Number);
  return (ty - fy) * 12 + tm - fm + 1;
}

export interface MonthOption {
  value: string;
  label: string;
}

/** The selectable months: the current one and the 23 before it, newest first. */
export function monthOptions(currentPeriod: string): MonthOption[] {
  return Array.from({ length: HORIZON_MONTHS }, (_, i) => {
    const value = shiftPeriod(currentPeriod, -i);
    return { value, label: periodLabel(value) };
  });
}

/**
 * Keeps a range valid after one end moved: within the horizon, not after the current month, at
 * most {@link MAX_MONTHS} long. The end that was not touched follows the one that was.
 */
export function clampRange(
  from: string,
  to: string,
  currentPeriod: string,
  moved: 'from' | 'to',
): { from: string; to: string } {
  const first = shiftPeriod(currentPeriod, 1 - HORIZON_MONTHS);
  const clamp = (p: string) => (p < first ? first : p > currentPeriod ? currentPeriod : p);
  let [f, t] = [clamp(from), clamp(to)];
  if (moved === 'from') {
    if (t < f) t = f;
    if (monthsBetween(f, t) > MAX_MONTHS) t = shiftPeriod(f, MAX_MONTHS - 1);
  } else {
    if (f > t) f = t;
    if (monthsBetween(f, t) > MAX_MONTHS) f = shiftPeriod(t, 1 - MAX_MONTHS);
  }
  return { from: clamp(f), to: clamp(t) };
}

/** The last <paramref name="months"/> months up to the current one. */
export function lastMonths(currentPeriod: string, months: number): { from: string; to: string } {
  return { from: shiftPeriod(currentPeriod, 1 - months), to: currentPeriod };
}

/** A row's visible name; groups without a coach have no id and no name. */
export function rowName(row: { id?: string | null; name: string }, by: string): string {
  if (by.toLowerCase() === 'coach' && !row.id) {
    return ui.reports.noCoach;
  }
  return row.name.trim() || ui.reports.noValue;
}

export function counted(cell: AttendanceCellDto): number {
  return count(cell.present) + count(cell.late) + count(cell.absent);
}

/** "%87,5" or "—" when nothing was counted. */
export function rateText(value: number | string | null | undefined): string {
  return percentText(value);
}

/** Plot value only; never shown to the user. */
function plot(amount: MoneyDto): number {
  return Number(amount.amount);
}

function plotRate(value: number | string | null | undefined): number | null {
  const r = ratio(value);
  return r === null ? null : Math.round(r * 1000) / 10;
}

function dataIndex(params: unknown): number {
  const first = (Array.isArray(params) ? params[0] : params) as { dataIndex?: number } | undefined;
  return first?.dataIndex ?? 0;
}

function legend(theme: ChartTheme): Record<string, unknown> {
  return {
    bottom: 0,
    type: 'scroll',
    icon: 'roundRect',
    itemWidth: 10,
    itemHeight: 10,
    textStyle: { color: theme.text },
  };
}

/** The rows with the most counted records, for the chart (ties by name order, as in the table). */
export function chartRows(rows: AttendanceRowDto[], limit = CHART_ROWS): AttendanceRowDto[] {
  return rows
    .map((row, index) => ({ row, index, weight: counted(row.total) }))
    .filter((r) => r.weight > 0)
    .sort((a, b) => b.weight - a.weight || a.index - b.index)
    .slice(0, limit)
    .map((r) => r.row);
}

/** Monthly attendance rate: the school as a thick line, the busiest rows as thin dashed lines. */
export function attendanceOption(
  report: AttendanceReportDto,
  theme: ChartTheme,
): EChartsCoreOption {
  const text = ui.reports;
  const rows = chartRows(report.rows);
  const school = {
    name: text.schoolTotal,
    type: 'line',
    symbol: 'circle',
    symbolSize: 8,
    connectNulls: false,
    data: report.total.months.map((m) => plotRate(m.rate)),
    itemStyle: { color: theme.series[0] },
    lineStyle: { color: theme.series[0], width: 3 },
  };
  const series = rows.map((row, i) => {
    const color = theme.series[(i + 1) % theme.series.length];
    return {
      name: rowName(row, report.by),
      type: 'line',
      symbol: 'emptyCircle',
      symbolSize: 6,
      connectNulls: false,
      data: row.months.map((m) => plotRate(m.rate)),
      itemStyle: { color },
      lineStyle: { color, width: 1.5, type: 'dashed' },
    };
  });
  return {
    legend: legend(theme),
    grid: { bottom: 48 },
    tooltip: {
      trigger: 'axis',
      formatter: (params: unknown) => {
        const index = dataIndex(params);
        const lines = [school, ...series].map((s, i) => {
          const source = i === 0 ? report.total : rows[i - 1];
          const cell = source.months[index];
          return `${escapeHtml(s.name)}: ${rateText(cell?.rate)}`;
        });
        return [escapeHtml(periodLabel(report.periods[index] ?? '')), ...lines].join('<br/>');
      },
    },
    xAxis: { type: 'category', data: report.periods.map(periodLabel) },
    // The plot is 0–100; the label is the locale's percent ("%50" in Turkish, "50%" in English).
    yAxis: {
      type: 'value',
      min: 0,
      max: 100,
      axisLabel: { formatter: (v: number) => percentText(v / 100) },
    },
    series: [school, ...series],
  };
}

export function attendanceSummary(report: AttendanceReportDto): string {
  return interpolate(ui.reports.attendance.summary, {
    from: periodLabel(report.from),
    to: periodLabel(report.to),
    rate: rateText(report.total.total.rate),
    count: formatNumber(counted(report.total.total)),
  });
}

/** Enrolled (bars) against capacity (line) of the whole selection per month. */
export function occupancyOption(report: OccupancyReportDto, theme: ChartTheme): EChartsCoreOption {
  const text = ui.reports.occupancy;
  const months = report.total.months;
  return {
    legend: legend(theme),
    grid: { bottom: 36 },
    tooltip: {
      trigger: 'axis',
      formatter: (params: unknown) => {
        const month = months[dataIndex(params)];
        return month
          ? `${escapeHtml(periodLabel(month.period))}<br/>${text.enrolled}: ${formatNumber(count(month.enrolled))}<br/>${text.capacity}: ${formatNumber(count(month.capacity))}<br/>${text.utilisation}: ${rateText(month.utilisation)}`
          : '';
      },
    },
    xAxis: { type: 'category', data: months.map((m) => periodLabel(m.period)) },
    yAxis: { type: 'value', minInterval: 1 },
    series: [
      {
        name: text.enrolled,
        type: 'bar',
        barMaxWidth: 28,
        data: months.map((m) => count(m.enrolled)),
        itemStyle: { color: theme.series[0], borderRadius: [4, 4, 0, 0] },
      },
      {
        name: text.capacity,
        type: 'line',
        step: 'middle',
        symbol: 'circle',
        symbolSize: 6,
        data: months.map((m) => count(m.capacity)),
        itemStyle: { color: theme.series[4] },
        lineStyle: { color: theme.series[4], width: 2 },
      },
    ],
  };
}

export function occupancySummary(report: OccupancyReportDto): string {
  const last = report.total.months.at(-1);
  return last
    ? interpolate(ui.reports.occupancy.summary, {
        period: periodLabel(last.period),
        enrolled: formatNumber(count(last.enrolled)),
        capacity: formatNumber(count(last.capacity)),
        rate: rateText(last.utilisation),
      })
    : ui.reports.occupancy.title;
}

/** Accrued and collected for the period (bars), collected by payment date (line). */
export function revenueOption(report: RevenueReportDto, theme: ChartTheme): EChartsCoreOption {
  const text = ui.reports.revenue;
  const periods = report.periods;
  return {
    legend: legend(theme),
    grid: { bottom: 36 },
    tooltip: {
      trigger: 'axis',
      formatter: (params: unknown) => {
        const p = periods[dataIndex(params)];
        return p
          ? [
              escapeHtml(periodLabel(p.period ?? '')),
              `${text.accrued}: ${displayMoney(p.accrued)}`,
              `${text.collectedForPeriod}: ${displayMoney(p.collectedForPeriod)}`,
              `${text.outstanding}: ${displayMoney(p.outstanding)}`,
              `${text.collectedInMonth}: ${displayMoney(p.collectedInMonth)}`,
            ].join('<br/>')
          : '';
      },
    },
    xAxis: { type: 'category', data: periods.map((p) => periodLabel(p.period ?? '')) },
    yAxis: {
      type: 'value',
      axisLabel: { formatter: (value: number) => `₺${formatNumber(value)}` },
    },
    series: [
      {
        name: text.accrued,
        type: 'bar',
        barMaxWidth: 24,
        data: periods.map((p) => plot(p.accrued)),
        itemStyle: { color: theme.series[1], borderRadius: [4, 4, 0, 0] },
      },
      {
        name: text.collectedForPeriod,
        type: 'bar',
        barMaxWidth: 24,
        data: periods.map((p) => plot(p.collectedForPeriod)),
        itemStyle: { color: theme.series[0], borderRadius: [4, 4, 0, 0] },
      },
      {
        name: text.collectedInMonth,
        type: 'line',
        symbol: 'diamond',
        symbolSize: 8,
        data: periods.map((p) => plot(p.collectedInMonth)),
        itemStyle: { color: theme.series[3] },
        lineStyle: { color: theme.series[3], width: 2, type: 'dashed' },
      },
    ],
  };
}

export function revenueSummary(report: RevenueReportDto): string {
  return interpolate(ui.reports.revenue.summary, {
    from: periodLabel(report.from),
    to: periodLabel(report.to),
    accrued: displayMoney(report.total.accrued),
    collected: displayMoney(report.total.collectedForPeriod),
    outstanding: displayMoney(report.total.outstanding),
  });
}

/** "1–30 gün", "91 gün ve üzeri". */
export function bucketLabel(bucket: {
  fromDays: number | string;
  toDays?: number | string | null;
}): string {
  const text = ui.reports.revenue.overdue;
  return bucket.toDays === null || bucket.toDays === undefined
    ? interpolate(text.daysOpen, { from: count(bucket.fromDays) })
    : interpolate(text.days, { from: count(bucket.fromDays), to: count(bucket.toDays) });
}

/** Share of a bucket in the overdue total, for the bar width (0–100). */
export function bucketShare(amount: MoneyDto, total: MoneyDto): number {
  const t = Number(total.amount);
  return t > 0 ? Math.min(100, (Number(amount.amount) / t) * 100) : 0;
}

export { percent, periodLabel };
