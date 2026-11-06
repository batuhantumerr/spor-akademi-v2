import { AbstractControl, ValidationErrors } from '@angular/forms';

/**
 * Mirrors the API's PhoneNumber rule: a Turkish number given as 10 digits, 0 + 10 digits or
 * +90 / 90 + 10 digits, starting with 2–5. Like the API, only space, '-', '(', ')', '.' and '+'
 * are ignored; tabs or other whitespace make the number invalid.
 */
export function isTurkishPhone(value: string): boolean {
  const trimmed = value.trim();
  if (trimmed.lastIndexOf('+') > 0) {
    return false;
  }
  const hasPlus = trimmed.startsWith('+');
  const digits = trimmed.replace(/[ \-().+]/g, '');
  if (!/^\d+$/.test(digits)) {
    return false;
  }
  let national: string | null = null;
  if (digits.length === 12 && digits.startsWith('90')) {
    national = digits.slice(2);
  } else if (digits.length === 11 && !hasPlus && digits.startsWith('0')) {
    national = digits.slice(1);
  } else if (digits.length === 10 && !hasPlus) {
    national = digits;
  }
  return national !== null && national[0] >= '2' && national[0] <= '5';
}

/** Optional Turkish phone: empty is fine, anything else must be a valid number. */
export function optionalTurkishPhone(
  control: AbstractControl<string | null>,
): ValidationErrors | null {
  const value = control.value ?? '';
  return value.trim() === '' || isTurkishPhone(value) ? null : { phone: true };
}
