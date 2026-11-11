/**
 * "Now" in the school's time zone (from the session's user), not the browser's: a parent abroad still sees the
 * school's day. The server stays authoritative for what "today" means in its answers.
 */
export interface SchoolNow {
  /** `YYYY-MM-DD` */
  readonly date: string;
  /** English weekday name, as the API's `DayOfWeek` (`Monday`…). */
  readonly day: string;
  /** Minutes since midnight. */
  readonly minutes: number;
}

export const WEEK = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'] as const;

export function schoolNow(now: Date, timeZone: string | null | undefined): SchoolNow {
  const options: Intl.DateTimeFormatOptions = {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    weekday: 'long',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  };
  let parts: Intl.DateTimeFormatPart[];
  try {
    parts = new Intl.DateTimeFormat('en-US', timeZone ? { ...options, timeZone } : options).formatToParts(
      now,
    );
  } catch {
    parts = new Intl.DateTimeFormat('en-US', options).formatToParts(now); // Unknown zone: the browser's.
  }
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? '';
  return {
    date: `${part('year')}-${part('month')}-${part('day')}`,
    day: part('weekday'),
    minutes: Number(part('hour')) * 60 + Number(part('minute')),
  };
}

/** `"17:30"` or `"17:30:00"` → 1050. */
export function minutesOf(time: string): number {
  const [hours = 0, minutes = 0] = time.split(':').map(Number);
  return hours * 60 + minutes;
}

export interface Slot {
  readonly day: string;
  readonly start: string;
  readonly end: string;
}

export interface UpcomingSlot<T> {
  readonly item: T;
  readonly slot: Slot;
  /** Days from today (0 = today). */
  readonly inDays: number;
  readonly inProgress: boolean;
}

/**
 * The next weekly slot that has not ended yet: today's later or running session, else the earliest one in the
 * following days. `items` pairs each slot with its owner (e.g. the group).
 */
export function nextSlot<T>(
  items: readonly { item: T; slots: readonly Slot[] }[],
  now: SchoolNow,
): UpcomingSlot<T> | null {
  const today = WEEK.indexOf(now.day as (typeof WEEK)[number]);
  if (today < 0) return null;
  let best: { upcoming: UpcomingSlot<T>; key: number } | null = null;
  for (const { item, slots } of items) {
    for (const slot of slots) {
      const day = WEEK.indexOf(slot.day as (typeof WEEK)[number]);
      if (day < 0) continue;
      const start = minutesOf(slot.start);
      const end = minutesOf(slot.end);
      // The API never returns such a slot: a weekly slot is a TimeRange whose end is after its start within one
      // day (TimeRange.Create refuses anything else, so there are no overnight sessions). Skipped, not misread.
      if (end <= start) continue;
      let inDays = (day - today + 7) % 7;
      if (inDays === 0 && end <= now.minutes) inDays = 7; // Today's session is over: next week.
      const key = inDays * 24 * 60 + start;
      if (!best || key < best.key) {
        const inProgress = inDays === 0 && start <= now.minutes && now.minutes < end;
        best = { upcoming: { item, slot, inDays, inProgress }, key };
      }
    }
  }
  return best?.upcoming ?? null;
}
