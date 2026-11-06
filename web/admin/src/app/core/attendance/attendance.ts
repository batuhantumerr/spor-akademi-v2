import { AttendanceStatus } from '../../api/models';

/** The four marks, in the order the coach sees them. */
export const STATUSES = ['Present', 'Absent', 'Late', 'Excused'] as const;
export type Mark = (typeof STATUSES)[number];

/** The API's session key: the planned start as <c>yyyy-MM-ddTHH:mm</c> (local school time). */
export function sessionKey(date: string, start: string): string {
  return `${date.slice(0, 10)}T${start.slice(0, 5)}`;
}

/** Splits a session key back into date and start time. */
export function parseSessionKey(key: string): { date: string; start: string } | null {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})$/.exec(key);
  return match ? { date: match[1], start: match[2] } : null;
}

export function isMark(status: AttendanceStatus | null | undefined): status is Mark {
  return status === 'Present' || status === 'Absent' || status === 'Late' || status === 'Excused';
}

/** Tailwind classes per status, used by the buttons and the month matrix. */
export const statusTone: Record<Mark, string> = {
  Present: 'bg-success/15 text-success border-success/40',
  Absent: 'bg-destructive/15 text-destructive border-destructive/40',
  Late: 'bg-warning/15 text-warning border-warning/40',
  Excused: 'bg-info/15 text-info border-info/40',
};
