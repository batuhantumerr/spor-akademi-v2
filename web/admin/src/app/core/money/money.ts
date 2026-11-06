import { MoneyDto } from '../../api/models';

/**
 * Money stays text end to end (DEC-066): amounts go to the API as "1250.00" and are shown as
 * "₺1.250,00" without ever passing through a JavaScript number. Sums use integer kuruş (BigInt).
 */
export const CURRENCY = 'TRY';

/** "1.250", "12.500.000": a leading group of 1–3 digits that does not start with 0. */
const THOUSANDS = /^[1-9]\d{0,2}(\.\d{3})+$/;

/** An API amount: digits, optional two decimals, optional leading minus for differences. */
const API_AMOUNT = /^-?\d+(\.\d{1,2})?$/;

/** Same bound as the API (Money.MaxAmount = 9 999 999 999.99). */
const MAX_INTEGER_DIGITS = 10;

/**
 * Reads what a Turkish user types: "1250", "1.250", "1.250,5", "1250,50" or "1250.50". Dots are
 * thousands separators only in a well-formed grouping that starts with 1–9 ("1.250", "12.500.000"),
 * so "0.500" or "00.500" are not read as 500. Returns the API form "1250.50", or null when the
 * text is not an amount (more than two decimals, letters, a sign…).
 */
export function parseMoneyInput(text: string): string | null {
  const value = text.trim().replace(/\s|₺/g, '');
  if (value === '') {
    return null;
  }
  let integer: string;
  let fraction = '';
  if (value.includes(',')) {
    // Turkish: comma is the decimal separator, dots group thousands.
    const [int, frac, ...rest] = value.split(',');
    if (rest.length > 0 || !(THOUSANDS.test(int) || /^\d+$/.test(int))) {
      return null;
    }
    integer = int.replace(/\./g, '');
    fraction = frac;
  } else if (THOUSANDS.test(value)) {
    integer = value.replace(/\./g, '');
  } else {
    const [int, frac, ...rest] = value.split('.');
    if (rest.length > 0) {
      return null;
    }
    integer = int;
    fraction = frac ?? '';
  }
  if (!/^\d+$/.test(integer) || !/^\d{0,2}$/.test(fraction)) {
    return null;
  }
  integer = integer.replace(/^0+(?=\d)/, '');
  if (integer.length > MAX_INTEGER_DIGITS) {
    return null;
  }
  return `${integer}.${fraction.padEnd(2, '0')}`;
}

/** "1250.5" or "1250.50" → 125050n kuruş; "-1.25" → -125n. Throws on anything else. */
export function toKurus(amount: string): bigint {
  if (!API_AMOUNT.test(amount)) {
    throw new Error(`Not an amount: ${amount}`);
  }
  const negative = amount.startsWith('-');
  const [integer, fraction = ''] = amount.replace(/^-/, '').split('.');
  const kurus = BigInt(integer) * 100n + BigInt((fraction + '00').slice(0, 2));
  return negative ? -kurus : kurus;
}

/** 125050n → "1250.50". */
export function fromKurus(kurus: bigint): string {
  const negative = kurus < 0n;
  const abs = negative ? -kurus : kurus;
  const text = `${abs / 100n}.${String(abs % 100n).padStart(2, '0')}`;
  return negative ? `-${text}` : text;
}

/** Exact sum of API amounts. */
export function sumAmounts(amounts: readonly string[]): string {
  return fromKurus(amounts.reduce((total, a) => total + toKurus(a), 0n));
}

export function isPositive(amount: string): boolean {
  return toKurus(amount) > 0n;
}

/** "1250.50" → "₺1.250,50" (grouping and comma decimals as in tr-TR, from the text itself). */
export function displayMoney(amount: string | MoneyDto | null | undefined): string {
  const text = typeof amount === 'string' ? amount : amount?.amount;
  if (text === null || text === undefined || !API_AMOUNT.test(text)) {
    return '—';
  }
  const kurus = toKurus(text);
  const normalized = fromKurus(kurus < 0n ? -kurus : kurus);
  const [integer, fraction] = normalized.split('.');
  const grouped = integer.replace(/\B(?=(\d{3})+(?!\d))/g, '.');
  return `${kurus < 0n ? '-' : ''}₺${grouped},${fraction}`;
}

export function money(amount: string): MoneyDto {
  return { amount, currency: CURRENCY };
}
