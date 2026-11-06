import { errorMessages, ui } from '../i18n/tr';
import { addDays, summarizeSchedule, validateSlots } from './schedule';

describe('validateSlots', () => {
  it('accepts well-formed, non-overlapping slots (touching ends are fine)', () => {
    const issues = validateSlots([
      { day: 'Tuesday', start: '17:00', end: '18:00' },
      { day: 'Tuesday', start: '18:00', end: '19:30' },
      { day: 'Thursday', start: '17:00', end: '18:00' },
    ]);
    expect(issues).toEqual([{}, {}, {}]);
  });

  it('requires 24-hour HH:mm', () => {
    const [issue] = validateSlots([{ day: 'Monday', start: '5:00', end: '25:00' }]);
    expect(issue.start).toBe(ui.groupDetail.timeInvalid);
    expect(issue.end).toBe(ui.groupDetail.timeInvalid);
  });

  it('requires the end after the start', () => {
    const [same, reversed] = validateSlots([
      { day: 'Monday', start: '17:00', end: '17:00' },
      { day: 'Friday', start: '18:00', end: '17:00' },
    ]);
    expect(same.end).toBe(errorMessages['TimeRange.Invalid']);
    expect(reversed.end).toBe(errorMessages['TimeRange.Invalid']);
  });

  it('flags both slots of a same-day overlap, but not other days', () => {
    const issues = validateSlots([
      { day: 'Monday', start: '17:00', end: '18:30' },
      { day: 'Monday', start: '18:00', end: '19:00' },
      { day: 'Tuesday', start: '17:30', end: '18:30' },
    ]);
    expect(issues[0].end).toBe(errorMessages['Schedule.SlotOverlap']);
    expect(issues[1].end).toBe(errorMessages['Schedule.SlotOverlap']);
    expect(issues[2]).toEqual({});
  });
});

describe('summarizeSchedule', () => {
  it('groups days that share a time, in Turkish week order', () => {
    expect(
      summarizeSchedule([
        { day: 'Saturday', start: '10:00', end: '11:00' },
        { day: 'Thursday', start: '17:00', end: '18:30' },
        { day: 'Tuesday', start: '17:00', end: '18:30' },
      ]),
    ).toBe('Sal, Per 17:00–18:30 · Cmt 10:00–11:00');
  });

  it('says when there is no schedule', () => {
    expect(summarizeSchedule([])).toBe(ui.groups.noSchedule);
  });
});

describe('addDays', () => {
  it('crosses month and year boundaries in local time', () => {
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
    expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
  });
});
