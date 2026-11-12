import { perLocale } from '../i18n/locale';

/**
 * Display formats from NFR-CMP-03 in the active language (P3-10): Turkish dates gg.AA.yyyy and money ₺1.250,00;
 * English (en-GB) dd/mm/yyyy and ₺1,250.00. Times are 24-hour in both.
 */

const dateFormatter = perLocale(
  (l) =>
    new Intl.DateTimeFormat(l, {
      day: '2-digit',
      month: '2-digit',
      year: 'numeric',
    }),
);
const moneyFormatter = perLocale(
  (l) =>
    new Intl.NumberFormat(l, {
      style: 'currency',
      // Money is always Turkish lira, in every UI language; narrowSymbol shows ₺ in English too.
      currency: 'TRY',
      currencyDisplay: 'narrowSymbol',
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }),
);
const numberFormatter = perLocale((l) => new Intl.NumberFormat(l));
const percentFormatter = perLocale(
  (l) =>
    new Intl.NumberFormat(l, {
      style: 'percent',
      maximumFractionDigits: 0,
    }),
);

/** Formats an ISO date (<c>YYYY-MM-DD</c>) without any time-zone shift. */
export function formatDate(isoDate: string | null | undefined): string {
  if (!isoDate) {
    return '—';
  }
  const [year, month, day] = isoDate.slice(0, 10).split('-').map(Number);
  return dateFormatter().format(new Date(year, month - 1, day));
}

/** Formats API money (<c>{ amount: "1250.00" }</c>) as <c>₺1.250,00</c>. */
export function formatMoney(amount: string | number | null | undefined): string {
  if (amount === null || amount === undefined || amount === '') {
    return '—';
  }
  // A decimal string is formatted exactly (Intl.NumberFormat v3 accepts numeric strings), so amounts never pass
  // through floats; the ES2022 lib only types number | bigint.
  return moneyFormatter().format(amount as unknown as number);
}

export function formatNumber(value: number): string {
  return numberFormatter().format(value);
}

export function formatPercent(ratio: number): string {
  return percentFormatter().format(ratio);
}

/** Shows an E.164 Turkish number (<c>+905321234567</c>) as <c>0532 123 45 67</c>. */
export function formatPhone(phone: string | null | undefined): string {
  if (!phone) {
    return '—';
  }
  const match = /^\+90(\d{3})(\d{3})(\d{2})(\d{2})$/.exec(phone);
  return match ? `0${match[1]} ${match[2]} ${match[3]} ${match[4]}` : phone;
}

/** <c>HH:mm</c> already; trims seconds if the server ever sends them. */
export function formatTime(time: string | null | undefined): string {
  return time ? time.slice(0, 5) : '—';
}
