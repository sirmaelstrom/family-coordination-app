import { describe, expect, it } from 'vitest';
import { parsePlanLocation, planSearch, planWeek, stepPlan } from './view';
import { addDays, dayTitle, isDateStr } from './dates';

const TODAY = '2026-11-17';
const q = (s: string) => new URLSearchParams(s);

describe('parsePlanLocation', () => {
  it('defaults a bare /meal-plan to week view on today', () => {
    expect(parsePlanLocation(q(''), TODAY)).toEqual({ view: 'week', date: TODAY });
  });

  it('reads a day-view link', () => {
    expect(parsePlanLocation(q('view=day&date=2026-11-26'), TODAY)).toEqual({
      view: 'day',
      date: '2026-11-26',
    });
  });

  it('falls back to today for a malformed or impossible date', () => {
    for (const bad of ['2026-02-31', '2026-11-6', 'nope', '2026-13-01', '']) {
      expect(parsePlanLocation(q(`view=day&date=${bad}`), TODAY).date).toBe(TODAY);
    }
  });

  it('treats an unknown view as week', () => {
    expect(parsePlanLocation(q('view=month&date=2026-11-26'), TODAY).view).toBe('week');
  });
});

describe('planSearch', () => {
  it('round-trips every location through the query string', () => {
    for (const loc of [
      { view: 'day', date: '2026-11-26' },
      { view: 'day', date: TODAY },
      { view: 'week', date: '2026-11-26' },
      { view: 'week', date: TODAY },
    ] as const) {
      expect(parsePlanLocation(q(planSearch(loc, TODAY).replace(/^\?/, '')), TODAY)).toEqual(loc);
    }
  });

  it('keeps plain /meal-plan for this week anchored on today', () => {
    expect(planSearch({ view: 'week', date: TODAY }, TODAY)).toBe('');
    expect(planSearch({ view: 'day', date: TODAY }, TODAY)).toBe(`?view=day&date=${TODAY}`);
  });
});

describe('stepPlan / planWeek', () => {
  it('steps a day in day view across a month and a week boundary', () => {
    expect(stepPlan({ view: 'day', date: '2026-11-30' }, 1)).toEqual({ view: 'day', date: '2026-12-01' });
    expect(stepPlan({ view: 'day', date: '2026-11-23' }, -1)).toEqual({ view: 'day', date: '2026-11-22' });
    expect(planWeek({ view: 'day', date: '2026-11-22' })).toBe('2026-11-16');
  });

  it('steps a week in week view and keeps the weekday', () => {
    expect(stepPlan({ view: 'week', date: '2026-11-26' }, 1)).toEqual({ view: 'week', date: '2026-12-03' });
    expect(planWeek({ view: 'week', date: '2026-11-26' })).toBe('2026-11-23');
  });

  it('steps across the US DST change without slipping a day', () => {
    // 2026-11-01 is the fall-back Sunday; 2026-03-08 the spring-forward one.
    expect(addDays('2026-10-31', 1)).toBe('2026-11-01');
    expect(addDays('2026-11-01', 1)).toBe('2026-11-02');
    expect(addDays('2026-03-08', -1)).toBe('2026-03-07');
  });
});

describe('date helpers', () => {
  it('validates real calendar dates only', () => {
    expect(isDateStr('2028-02-29')).toBe(true);
    expect(isDateStr('2026-02-29')).toBe(false);
    expect(isDateStr(null)).toBe(false);
  });

  it('titles the Thanksgiving day view', () => {
    expect(dayTitle('2026-11-26')).toBe('Thursday, November 26, 2026');
  });
});
