import { dateIn } from './school-clock';

describe('dateIn', () => {
  const instant = new Date('2027-01-14T22:30:00Z');

  it('gives the calendar date in the given zone', () => {
    expect(dateIn(instant, 'Europe/Istanbul')).toBe('2027-01-15');
    expect(dateIn(instant, 'America/New_York')).toBe('2027-01-14');
    expect(dateIn(instant, 'UTC')).toBe('2027-01-14');
  });

  it('falls back to the browser date for an unknown or missing zone', () => {
    const local = dateIn(instant, null);
    expect(local).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(dateIn(instant, 'Not/AZone')).toBe(local);
  });
});
