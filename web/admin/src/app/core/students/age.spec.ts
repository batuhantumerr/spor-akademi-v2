import { ageOn, isMinor, todayIso } from './age';

describe('ageOn (same rule as the API)', () => {
  it('counts the birthday itself as a year older', () => {
    expect(ageOn('2008-11-06', '2026-11-06')).toBe(18);
    expect(ageOn('2008-11-07', '2026-11-06')).toBe(17);
  });

  it('lets a 29 February birthday turn a year older on 1 March', () => {
    expect(ageOn('2008-02-29', '2026-02-28')).toBe(17);
    expect(ageOn('2008-02-29', '2026-03-01')).toBe(18);
  });

  it('rejects unusable or future dates', () => {
    expect(ageOn('', '2026-11-06')).toBeNull();
    expect(ageOn('2030-01-01', '2026-11-06')).toBeNull();
  });

  it('treats under 18 as minor', () => {
    expect(isMinor(17)).toBe(true);
    expect(isMinor(18)).toBe(false);
  });

  it('formats the local date', () => {
    expect(todayIso(new Date(2026, 0, 5))).toBe('2026-01-05');
  });
});
