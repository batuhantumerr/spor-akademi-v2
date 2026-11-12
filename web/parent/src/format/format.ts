/** Display formats (NFR-CMP-03, same as web/admin core/format): dates gg.AA.yyyy, 24-hour times, money ₺1.250,00. */

const LOCALE = 'tr-TR';

const dateFormatter = new Intl.DateTimeFormat(LOCALE, { day: '2-digit', month: '2-digit', year: 'numeric' });
const dayMonthFormatter = new Intl.DateTimeFormat(LOCALE, { day: 'numeric', month: 'long' });
const periodFormatter = new Intl.DateTimeFormat(LOCALE, { month: 'long', year: 'numeric' });
const dateTimeOptions: Intl.DateTimeFormatOptions = {
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
};
const moneyFormatter = new Intl.NumberFormat(LOCALE, {
  style: 'currency',
  currency: 'TRY',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});
const percentFormatter = new Intl.NumberFormat(LOCALE, { style: 'percent', maximumFractionDigits: 0 });

function localDate(isoDate: string): Date {
  const [year = 1970, month = 1, day = 1] = isoDate.slice(0, 10).split('-').map(Number);
  return new Date(year, month - 1, day);
}

/** An ISO date (`YYYY-MM-DD`) without any time-zone shift. */
export function formatDate(isoDate: string | null | undefined): string {
  return isoDate ? dateFormatter.format(localDate(isoDate)) : '—';
}

/** An ISO date as "10 Ocak" (short notices about a recent day). */
export function formatDayMonth(isoDate: string): string {
  return dayMonthFormatter.format(localDate(isoDate));
}

/** An instant (`2026-11-11T08:00:00Z`) in the given IANA zone (the school's), or the browser's. */
export function formatDateTime(instant: string, timeZone?: string | null): string {
  const date = new Date(instant);
  if (timeZone) {
    try {
      return new Intl.DateTimeFormat(LOCALE, { ...dateTimeOptions, timeZone }).format(date);
    } catch {
      // Unknown zone in this browser: the local time below.
    }
  }
  return new Intl.DateTimeFormat(LOCALE, dateTimeOptions).format(date);
}

/** A billing period `YYYY-MM` as "Kasım 2026". */
export function formatPeriod(period: string): string {
  return /^\d{4}-\d{2}$/.test(period) ? periodFormatter.format(localDate(`${period}-01`)) : period;
}

/** `HH:mm`; trims seconds if the server ever sends them. */
export function formatTime(time: string | null | undefined): string {
  return time ? time.slice(0, 5) : '—';
}

/** API money (text, DEC-066) as `₺1.250,00`; never through floating-point sums. */
export function formatMoney(amount: string | null | undefined): string {
  return amount ? moneyFormatter.format(Number(amount)) : '—';
}

export function formatPercent(ratio: number): string {
  return percentFormatter.format(ratio);
}

/** `"1250.5"` → 125050n kuruş. */
export function toKurus(amount: string): bigint {
  const match = /^(-?)(\d+)(?:\.(\d{1,2}))?$/.exec(amount.trim());
  if (!match) throw new Error(`Not an amount: ${amount}`);
  const [, sign = '', integer = '0', fraction = ''] = match;
  const kurus = BigInt(integer) * 100n + BigInt(fraction.padEnd(2, '0'));
  return sign === '-' ? -kurus : kurus;
}

/** 125050n → `"1250.50"`. */
export function fromKurus(kurus: bigint): string {
  const negative = kurus < 0n;
  const abs = negative ? -kurus : kurus;
  const fraction = String(abs % 100n).padStart(2, '0');
  return `${negative ? '-' : ''}${String(abs / 100n)}.${fraction}`;
}

/** Exact sum of API amounts. */
export function sumMoney(amounts: readonly string[]): string {
  return fromKurus(amounts.reduce((total, amount) => total + toKurus(amount), 0n));
}
