import { perLocale } from '../../core/i18n/locale';

const periodFormatter = perLocale(
  (l) => new Intl.DateTimeFormat(l, { month: 'long', year: 'numeric' }),
);

/** "2026-11" → "Kasım 2026". */
export function periodLabel(period: string): string {
  const match = /^(\d{4})-(\d{2})$/.exec(period);
  if (!match) {
    return period;
  }
  return periodFormatter().format(new Date(Number(match[1]), Number(match[2]) - 1, 1));
}
