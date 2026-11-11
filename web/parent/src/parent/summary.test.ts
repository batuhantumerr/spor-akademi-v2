import { describe, expect, it } from 'vitest';
import { formatMoney, formatPeriod, sumMoney } from '../format/format';
import { schoolNow } from '../format/school-time';
import { attendance, due, group } from '../test/fake-api';
import { checkoutTarget, resultStatus } from './checkout';
import { nextTraining, owedTotal, todayAttendance } from './summary';

// Wednesday 2026-11-11, 15:30 in Istanbul (UTC+3).
const wednesdayAfternoon = schoolNow(new Date('2026-11-11T12:30:00Z'), 'Europe/Istanbul');

describe('school time', () => {
  it("reads the day and time in the school's zone, not the browser's", () => {
    expect(wednesdayAfternoon).toEqual({ date: '2026-11-11', day: 'Wednesday', minutes: 15 * 60 + 30 });
    // 22:30 UTC on Wednesday is already Thursday in Istanbul.
    expect(schoolNow(new Date('2026-11-11T22:30:00Z'), 'Europe/Istanbul')).toMatchObject({
      date: '2026-11-12',
      day: 'Thursday',
    });
  });
});

describe('next training', () => {
  it("is today's later session", () => {
    expect(nextTraining([group()], wednesdayAfternoon)).toEqual({
      when: 'Bugün 17:00–18:00',
      where: 'U10 Yüzme · Kapalı Havuz',
      isToday: true,
    });
  });

  it('says a session is running now', () => {
    const now = schoolNow(new Date('2026-11-11T14:15:00Z'), 'Europe/Istanbul'); // 17:15
    expect(nextTraining([group()], now)?.when).toBe('Şu anda: 17:00–18:00');
  });

  it('moves to the next day of the week once today is over, across groups', () => {
    const evening = schoolNow(new Date('2026-11-11T16:00:00Z'), 'Europe/Istanbul'); // 19:00
    const groups = [
      group(),
      group({
        enrollmentId: 'e2',
        groupName: 'U10 Basketbol',
        facilityName: 'Spor Salonu',
        slots: [
          { day: 'Saturday', start: '10:00', end: '11:00' },
          { day: 'Friday', start: '18:00', end: '19:00' },
        ],
      }),
    ];
    expect(nextTraining(groups, evening)).toEqual({
      when: 'Cuma 18:00–19:00',
      where: 'U10 Basketbol · Spor Salonu',
      isToday: false,
    });
  });

  it('wraps to next week for the only, already finished session', () => {
    const evening = schoolNow(new Date('2026-11-11T16:00:00Z'), 'Europe/Istanbul');
    expect(nextTraining([group()], evening)?.when).toBe('Çarşamba 17:00–18:00');
  });

  it('is null without any weekly slot', () => {
    expect(nextTraining([group({ slots: [] })], wednesdayAfternoon)).toBeNull();
  });
});

describe("today's attendance", () => {
  const session = (date: string, start: string, status: 'Present' | 'Absent' | null) => ({
    groupId: 'g',
    groupName: 'U10 Yüzme',
    sessionKey: `${date}T${start}`,
    date,
    start,
    end: '18:00',
    status,
  });

  it("is the status of today's latest session", () => {
    const data = attendance([
      session('2026-11-11', '09:00', 'Present'),
      session('2026-11-11', '17:00', 'Absent'),
      session('2026-11-10', '17:00', 'Present'),
    ]);
    expect(todayAttendance(data, '2026-11-11')).toBe('Absent');
  });

  it('is "not recorded" before the coach saves it, and null without a session today', () => {
    expect(todayAttendance(attendance([session('2026-11-11', '17:00', null)]), '2026-11-11')).toBe(
      'NotRecorded',
    );
    expect(todayAttendance(attendance([session('2026-11-10', '17:00', 'Present')]), '2026-11-11')).toBeNull();
  });
});

describe('money', () => {
  it('sums text amounts exactly and formats them in Turkish', () => {
    expect(sumMoney(['0.10', '0.20', '1249.70'])).toBe('1250.00');
    expect(formatMoney('1250.00')).toBe('₺1.250,00');
    expect(
      owedTotal([due(), due({ period: '2026-10', remaining: { amount: '450.50', currency: 'TRY' } })]),
    ).toBe('1350.50');
    expect(owedTotal([])).toBeNull();
  });

  it('shows a billing period as month and year', () => {
    expect(formatPeriod('2026-11')).toBe('Kasım 2026');
  });
});

describe('checkout', () => {
  it('follows only this origin or an https provider page', () => {
    expect(checkoutTarget('/sandbox-odeme?token=abc')).toBe(
      `${window.location.origin}/sandbox-odeme?token=abc`,
    );
    expect(checkoutTarget('https://sandbox-cpp.iyzipay.com/?token=abc')).toBe(
      'https://sandbox-cpp.iyzipay.com/?token=abc',
    );
    expect(checkoutTarget('javascript:alert(1)')).toBeNull();
    expect(checkoutTarget('http://example.test/pay')).toBeNull();
    expect(checkoutTarget(null)).toBeNull();
  });

  it('reads only known result statuses', () => {
    expect(resultStatus('Succeeded')).toBe('Succeeded');
    expect(resultStatus('Created')).toBe('Pending');
    expect(resultStatus('<script>')).toBe('Unknown');
    expect(resultStatus(null)).toBe('Unknown');
  });
});
