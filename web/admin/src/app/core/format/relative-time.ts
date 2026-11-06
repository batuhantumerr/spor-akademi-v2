import { LOCALE } from '../i18n/locale';

const relative = new Intl.RelativeTimeFormat(LOCALE, { numeric: 'auto' });
const absolute = new Intl.DateTimeFormat(LOCALE, {
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/**
 * "şimdi", "5 dakika önce", "3 saat önce", "dün", "4 gün önce"; older than a week falls back to
 * the date (gg.AA.yyyy SS:dd). Future instants (clock skew) read as "şimdi".
 */
export function relativeTime(iso: string, now: Date = new Date()): string {
  const then = new Date(iso);
  const diff = now.getTime() - then.getTime();
  if (Number.isNaN(diff)) {
    return '';
  }
  if (diff < MINUTE) {
    return relative.format(0, 'second');
  }
  if (diff < HOUR) {
    return relative.format(-Math.floor(diff / MINUTE), 'minute');
  }
  if (diff < DAY) {
    return relative.format(-Math.floor(diff / HOUR), 'hour');
  }
  if (diff < 7 * DAY) {
    return relative.format(-Math.floor(diff / DAY), 'day');
  }
  return absoluteTime(iso);
}

/** "14.01.2027 17:05" for tooltips and the <time> element. */
export function absoluteTime(iso: string): string {
  return absolute.format(new Date(iso)).replace(',', '');
}
