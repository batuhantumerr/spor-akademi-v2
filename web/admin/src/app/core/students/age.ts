/** Same threshold as the API (Student.AdultAge). */
export const ADULT_AGE = 18;

/**
 * Whole years between two ISO dates, compared by (month, day) like the API, so a 29 February
 * birthday turns a year older on 1 March in a non-leap year. Null for an unusable date.
 */
export function ageOn(birthIso: string, onIso: string): number | null {
  const birth = /^(\d{4})-(\d{2})-(\d{2})$/.exec(birthIso);
  const on = /^(\d{4})-(\d{2})-(\d{2})$/.exec(onIso);
  if (!birth || !on) {
    return null;
  }
  const [, by, bm, bd] = birth.map(Number);
  const [, oy, om, od] = on.map(Number);
  let age = oy - by;
  if (om < bm || (om === bm && od < bd)) {
    age--;
  }
  return age >= 0 ? age : null;
}

export function isMinor(age: number): boolean {
  return age < ADULT_AGE;
}
