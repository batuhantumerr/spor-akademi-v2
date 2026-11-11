import { format, text } from '../i18n/tr';
import { formatTime, sumMoney } from '../format/format';
import { nextSlot, type SchoolNow } from '../format/school-time';
import type { Child, ChildAttendance, ChildGroup, Due } from './queries';

/**
 * What a parent checks on a phone in ten seconds (P3-06): the next training, today's attendance and what is owed.
 * Pure functions over the API's answers, so the cards and the detail page say the same thing.
 */

export interface TrainingSummary {
  readonly when: string;
  readonly where: string;
  readonly isToday: boolean;
}

export function nextTraining(groups: readonly ChildGroup[], now: SchoolNow): TrainingSummary | null {
  const upcoming = nextSlot(
    groups.map((group) => ({ item: group, slots: group.slots })),
    now,
  );
  if (!upcoming) return null;
  const times = { start: formatTime(upcoming.slot.start), end: formatTime(upcoming.slot.end) };
  const when = upcoming.inProgress
    ? format(text.training.inProgress, times)
    : upcoming.inDays === 0
      ? format(text.training.today, times)
      : format(text.training.onDay, { ...times, day: text.days[upcoming.slot.day] ?? upcoming.slot.day });
  return {
    when,
    where: format(text.training.place, {
      group: upcoming.item.groupName,
      facility: upcoming.item.facilityName,
    }),
    isToday: upcoming.inDays === 0,
  };
}

export type TodayStatus = 'Present' | 'Late' | 'Absent' | 'Excused' | 'NotRecorded';

/** Today's attendance in the school's day: the latest of today's sessions, or null when there was none. */
export function todayAttendance(attendance: ChildAttendance, today: string): TodayStatus | null {
  const sessions = attendance.sessions
    .filter((session) => session.date === today)
    .sort((a, b) => a.start.localeCompare(b.start));
  const last = sessions.at(-1);
  if (!last) return null;
  return last.status ?? 'NotRecorded';
}

/** Exact total of the remaining amounts (text money, DEC-066); null when nothing is owed. */
export function owedTotal(dues: readonly Due[]): string | null {
  const remaining = dues.map((due) => due.remaining.amount);
  if (remaining.length === 0) return null;
  const total = sumMoney(remaining);
  return /^-?0\.00$/.test(total) ? null : total;
}

export function childName(child: Pick<Child, 'firstName' | 'lastName'>): string {
  return `${child.firstName} ${child.lastName}`;
}
