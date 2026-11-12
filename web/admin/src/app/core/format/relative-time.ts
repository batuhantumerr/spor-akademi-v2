import { locale, perLocale } from '../i18n/locale';
import { dateIn } from '../time/school-clock';

/** "şimdi", "dün"… only where they read naturally; otherwise "2 gün önce" (not "evvelsi gün"). */
const natural = perLocale((l) => new Intl.RelativeTimeFormat(l, { numeric: 'auto' }));
const relative = perLocale((l) => new Intl.RelativeTimeFormat(l, { numeric: 'always' }));

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY_MS = 24 * HOUR;

/** Whole calendar days between two ISO dates (<c>YYYY-MM-DD</c>). */
function calendarDays(from: string, to: string): number {
  const utc = (iso: string) => {
    const [y, m, d] = iso.split('-').map(Number);
    return Date.UTC(y, m - 1, d);
  };
  return Math.round((utc(to) - utc(from)) / DAY_MS);
}

/**
 * "şimdi", "5 dakika önce", "3 saat önce", "dün", "4 gün önce"; a week or more falls back to the
 * date. Days are calendar days in the school's time zone (so "dün" means the school's yesterday,
 * whatever the browser's zone), and the fallback date is in that zone too. Future instants (clock
 * skew) read as "şimdi".
 */
export function relativeTime(
  iso: string,
  now: Date = new Date(),
  timeZone: string | null = null,
): string {
  const then = new Date(iso);
  const diff = now.getTime() - then.getTime();
  if (Number.isNaN(diff)) {
    return '';
  }
  if (diff < MINUTE) {
    return natural().format(0, 'second');
  }
  const days = calendarDays(dateIn(then, timeZone), dateIn(now, timeZone));
  if (days === 0) {
    return diff < HOUR
      ? relative().format(-Math.floor(diff / MINUTE), 'minute')
      : relative().format(-Math.floor(diff / HOUR), 'hour');
  }
  if (days === 1) {
    return natural().format(-1, 'day');
  }
  if (days < 7) {
    return relative().format(-days, 'day');
  }
  return absoluteTime(iso, timeZone);
}

/** "14.01.2027 17:05" in the school's time zone, for tooltips and the fallback. */
export function absoluteTime(iso: string, timeZone: string | null = null): string {
  const options: Intl.DateTimeFormatOptions = {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  };
  try {
    return new Intl.DateTimeFormat(locale(), timeZone ? { ...options, timeZone } : options)
      .format(new Date(iso))
      .replace(',', '');
  } catch {
    return new Intl.DateTimeFormat(locale(), options).format(new Date(iso)).replace(',', '');
  }
}
