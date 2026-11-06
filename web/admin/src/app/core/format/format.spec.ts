import { formatDate, formatMoney, formatTime } from './format';

describe('formats (NFR-CMP-03)', () => {
  it('formats dates as gg.AA.yyyy without time-zone shifts', () => {
    expect(formatDate('2027-01-04')).toBe('04.01.2027');
  });

  it('formats money as Turkish lira', () => {
    expect(formatMoney('1250.00').replace(/\s/g, '')).toBe('₺1.250,00');
  });

  it('shows a dash for missing values', () => {
    expect(formatDate(null)).toBe('—');
    expect(formatMoney(undefined)).toBe('—');
  });

  it('trims seconds from times', () => {
    expect(formatTime('17:00:00')).toBe('17:00');
  });
});
