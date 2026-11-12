import type { QueryClient, QueryKey } from '@tanstack/react-query';
import { formatDayMonth } from '../format/format';
import { format, text } from '../i18n/tr';
import { parentKeys, type Child } from '../parent/queries';

/**
 * What the notifications hub sends (P3-08): ids only, never display data. The app refetches the REST queries it
 * already has and builds the toast text from its own cache; only `status` is taken from the payload.
 */
export type NotificationType = 'announcement.published' | 'attendance.recorded' | 'payment.recorded';
export type AttendanceStatus = 'Present' | 'Absent' | 'Late' | 'Excused';

export interface ParentNotification {
  readonly type: NotificationType;
  readonly id: string;
  readonly groupId: string | null;
  readonly studentId: string | null;
  /** `YYYY-MM-DD` (attendance: the session's date). */
  readonly date: string | null;
  readonly status: AttendanceStatus | null;
}

const TYPES: readonly string[] = ['announcement.published', 'attendance.recorded', 'payment.recorded'];
const STATUSES: readonly string[] = ['Present', 'Absent', 'Late', 'Excused'];

/** The hub's `notify` argument, or null when it is not one this app understands (ignored, never shown). */
export function parseNotification(value: unknown): ParentNotification | null {
  if (typeof value !== 'object' || value === null) return null;
  const raw = value as { readonly [K in keyof ParentNotification]?: unknown };
  if (typeof raw.type !== 'string' || !TYPES.includes(raw.type)) return null;
  if (typeof raw.id !== 'string' || raw.id === '') return null;
  const optional = (field: unknown) => (typeof field === 'string' && field !== '' ? field : null);
  const date = optional(raw.date);
  const status = optional(raw.status);
  return {
    type: raw.type as NotificationType,
    id: raw.id,
    groupId: optional(raw.groupId),
    studentId: optional(raw.studentId),
    date: date && /^\d{4}-\d{2}-\d{2}$/.test(date) ? date : null,
    status: status && STATUSES.includes(status) ? (status as AttendanceStatus) : null,
  };
}

/** The per-child lists a notification can change; the schedule never does. */
const LIVE_LISTS = ['attendance', 'dues', 'payments', 'announcements'] as const;
type LiveList = (typeof LIVE_LISTS)[number];

/** `['parent', 'child', <id>, <list>]` of any child. */
function isChildList(key: QueryKey, lists: readonly LiveList[]): boolean {
  return (
    key[0] === parentKeys.all[0] &&
    key[1] === 'child' &&
    typeof key[3] === 'string' &&
    (lists as readonly string[]).includes(key[3])
  );
}

/**
 * After every (re)connect: events sent while the app was not connected are lost, so everything a notification could
 * have changed is refetched (active queries now, the rest when next used).
 */
export function invalidateAfterConnect(queryClient: QueryClient): Promise<void> {
  return queryClient.invalidateQueries({
    predicate: ({ queryKey }) =>
      (queryKey[0] === parentKeys.all[0] && queryKey[1] === 'children') || isChildList(queryKey, LIVE_LISTS),
  });
}

/** Refetches exactly what one notification changed. */
export function invalidateFor(queryClient: QueryClient, notification: ParentNotification): Promise<void> {
  const child = notification.studentId;
  switch (notification.type) {
    case 'announcement.published':
      // The payload names a group; which child is in it is the API's answer, so every child's list is refetched.
      return queryClient.invalidateQueries({
        predicate: ({ queryKey }) => isChildList(queryKey, ['announcements']),
      });
    case 'attendance.recorded':
      return child
        ? queryClient.invalidateQueries({ queryKey: parentKeys.attendance(child) })
        : queryClient.invalidateQueries({
            predicate: ({ queryKey }) => isChildList(queryKey, ['attendance']),
          });
    case 'payment.recorded':
      return child
        ? Promise.all([
            queryClient.invalidateQueries({ queryKey: parentKeys.dues(child) }),
            queryClient.invalidateQueries({ queryKey: parentKeys.payments(child) }),
          ]).then(() => undefined)
        : queryClient.invalidateQueries({
            predicate: ({ queryKey }) => isChildList(queryKey, ['dues', 'payments']),
          });
  }
}

/** The child's first name from the children list the app already loaded; never from the payload. */
export function childFirstName(queryClient: QueryClient, studentId: string | null): string {
  const children = queryClient.getQueryData<readonly Child[]>(parentKeys.children());
  const child = studentId ? children?.find((candidate) => candidate.id === studentId) : undefined;
  return child?.firstName ?? text.realtime.yourChild;
}

/** The toast text; `today` is the school's `YYYY-MM-DD`. */
export function notificationMessage(notification: ParentNotification, name: string, today: string): string {
  switch (notification.type) {
    case 'announcement.published':
      return text.realtime.announcement;
    case 'payment.recorded':
      return format(text.realtime.payment, { name });
    case 'attendance.recorded': {
      if (!notification.status) return format(text.realtime.attendanceUpdated, { name });
      if (!notification.date || notification.date === today) {
        return format(text.realtime.attendanceToday[notification.status], { name });
      }
      return format(text.realtime.attendanceOnDate[notification.status], {
        name,
        date: formatDayMonth(notification.date),
      });
    }
  }
}
