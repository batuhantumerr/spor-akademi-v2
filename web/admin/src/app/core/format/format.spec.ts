import { formatDate, formatMoney, formatPhone, formatTime } from './format';

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

  it('shows Turkish phone numbers in the national format', () => {
    expect(formatPhone('+905321234567')).toBe('0532 123 45 67');
    expect(formatPhone(null)).toBe('—');
    expect(formatPhone('12345')).toBe('12345');
  });

  it('trims seconds from times', () => {
    expect(formatTime('17:00:00')).toBe('17:00');
  });
});
