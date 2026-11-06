import { LOCALE } from '../i18n/locale';

/** Display formats from NFR-CMP-03: dates gg.AA.yyyy, 24-hour times, money ₺1.250,00. */

const dateFormatter = new Intl.DateTimeFormat(LOCALE, {
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
});
const moneyFormatter = new Intl.NumberFormat(LOCALE, {
  style: 'currency',
  currency: 'TRY',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});
const numberFormatter = new Intl.NumberFormat(LOCALE);
const percentFormatter = new Intl.NumberFormat(LOCALE, {
  style: 'percent',
  maximumFractionDigits: 0,
});

/** Formats an ISO date (<c>YYYY-MM-DD</c>) without any time-zone shift. */
export function formatDate(isoDate: string | null | undefined): string {
  if (!isoDate) {
    return '—';
  }
  const [year, month, day] = isoDate.slice(0, 10).split('-').map(Number);
  return dateFormatter.format(new Date(year, month - 1, day));
}

/** Formats API money (<c>{ amount: "1250.00" }</c>) as <c>₺1.250,00</c>. */
export function formatMoney(amount: string | number | null | undefined): string {
  if (amount === null || amount === undefined || amount === '') {
    return '—';
  }
  return moneyFormatter.format(typeof amount === 'string' ? Number(amount) : amount);
}

export function formatNumber(value: number): string {
  return numberFormatter.format(value);
}

export function formatPercent(ratio: number): string {
  return percentFormatter.format(ratio);
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
