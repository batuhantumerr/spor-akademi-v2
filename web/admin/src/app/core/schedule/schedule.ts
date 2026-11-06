import { DayOfWeek, ScheduleSlotDto } from '../../api/models';
import { errorMessages, interpolate, ui } from '../i18n/tr';
import { LOCALE } from '../i18n/locale';

/** Turkish week order (Monday first). */
export const WEEK: readonly DayOfWeek[] = [
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
  'Sunday',
];

/** Same limit as the API (Group.MaxScheduleSlots). */
export const MAX_SLOTS = 21;

const TIME = /^([01]\d|2[0-3]):[0-5]\d$/;

export interface SlotDraft {
  day: DayOfWeek;
  start: string;
  end: string;
}

/** Problems of one slot row; empty when the row is fine. */
export interface SlotIssues {
  start?: string;
  end?: string;
  /** Indexes of the rows this one overlaps (same day), so both rows can show the problem. */
  overlapsWith?: number[];
}

export function isTime(value: string): boolean {
  return TIME.test(value);
}

function minutes(time: string): number {
  const [h, m] = time.split(':').map(Number);
  return h * 60 + m;
}

function byDayAndStart(a: SlotDraft, b: SlotDraft): number {
  return WEEK.indexOf(a.day) - WEEK.indexOf(b.day) || a.start.localeCompare(b.start);
}

export function sortSlots<T extends SlotDraft>(slots: readonly T[]): T[] {
  return [...slots].sort(byDayAndStart);
}

/**
 * Client-side check mirroring the API: HH:mm (24h), end after start, and no two slots of the same
 * day overlapping (touching ends such as 17:00–18:00 and 18:00–19:00 are fine). Returns one entry
 * per slot index.
 */
export function validateSlots(slots: readonly SlotDraft[]): SlotIssues[] {
  const issues: SlotIssues[] = slots.map(() => ({}));
  slots.forEach((slot, i) => {
    if (!isTime(slot.start)) {
      issues[i].start = ui.groupDetail.timeInvalid;
    }
    if (!isTime(slot.end)) {
      issues[i].end = ui.groupDetail.timeInvalid;
    } else if (isTime(slot.start) && minutes(slot.end) <= minutes(slot.start)) {
      issues[i].end = errorMessages['TimeRange.Invalid'];
    }
  });
  // Only rows with a usable time range take part in the overlap check.
  const usable = issues.map((issue) => !issue.start && !issue.end);
  for (let i = 0; i < slots.length; i++) {
    for (let j = i + 1; j < slots.length; j++) {
      const a = slots[i];
      const b = slots[j];
      if (a.day !== b.day || !usable[i] || !usable[j]) {
        continue;
      }
      if (minutes(a.start) < minutes(b.end) && minutes(b.start) < minutes(a.end)) {
        for (const [k, other] of [
          [i, j],
          [j, i],
        ]) {
          issues[k].end ??= errorMessages['Schedule.SlotOverlap'];
          issues[k].overlapsWith = [...(issues[k].overlapsWith ?? []), other];
        }
      }
    }
  }
  return issues;
}

export function hasIssues(issues: readonly SlotIssues[]): boolean {
  return issues.some((i) => i.start !== undefined || i.end !== undefined);
}

/** "Sal, Per 17:00–18:30 · Cmt 10:00–11:00": days sharing a time range are listed together. */
export function summarizeSchedule(slots: readonly ScheduleSlotDto[]): string {
  if (slots.length === 0) {
    return ui.groups.noSchedule;
  }
  const groups = new Map<string, DayOfWeek[]>();
  for (const slot of sortSlots(slots)) {
    const range = `${slot.start}–${slot.end}`;
    groups.set(range, [...(groups.get(range) ?? []), slot.day]);
  }
  return [...groups.entries()]
    .sort(([, a], [, b]) => WEEK.indexOf(a[0]) - WEEK.indexOf(b[0]))
    .map(([range, days]) => `${days.map((d) => ui.days[d].short).join(', ')} ${range}`)
    .join(' · ');
}

/** Local calendar date as <c>YYYY-MM-DD</c> (no UTC shift). */
export function isoDate(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

export function addDays(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  return isoDate(new Date(y, m - 1, d + days));
}

const weekdayFormatter = new Intl.DateTimeFormat(LOCALE, { weekday: 'long' });

/** "Salı" for an ISO date. */
export function weekdayName(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number);
  return weekdayFormatter.format(new Date(y, m - 1, d));
}

export function slotLabel(index: number): string {
  return interpolate(ui.groupDetail.slotLabel, { n: index + 1 });
}
