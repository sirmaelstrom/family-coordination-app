import { describe, expect, it } from 'vitest';
import { parsePlanLocation, planSearch, planWeek, stepPlan, type PlanView } from './view';
import { addDays, dayTitle, isDateStr } from './dates';

const TODAY = '2026-11-17';
const q = (s: string) => new URLSearchParams(s);

describe('parsePlanLocation', () => {
  it('opens a bare /meal-plan on today in the screen default (week desktop, day phone)', () => {
    expect(parsePlanLocation(q(''), TODAY, 'week')).toEqual({ view: 'week', date: TODAY });
    expect(parsePlanLocation(q(''), TODAY, 'day')).toEqual({ view: 'day', date: TODAY });
  });

  it('reads a day-view link the same on every screen', () => {
    for (const def of ['week', 'day'] as PlanView[]) {
      expect(parsePlanLocation(q('view=day&date=2026-11-26'), TODAY, def)).toEqual({
        view: 'day',
        date: '2026-11-26',
      });
      expect(parsePlanLocation(q('view=week&date=2026-11-26'), TODAY, def).view).toBe('week');
    }
  });

  it('falls back to today for a malformed or impossible date', () => {
    for (const bad of ['2026-02-31', '2026-11-6', 'nope', '2026-13-01', '']) {
      expect(parsePlanLocation(q(`view=day&date=${bad}`), TODAY, 'week').date).toBe(TODAY);
    }
  });

  it('treats an unknown view as the default', () => {
    expect(parsePlanLocation(q('view=month&date=2026-11-26'), TODAY, 'week').view).toBe('week');
    expect(parsePlanLocation(q('view=month&date=2026-11-26'), TODAY, 'day').view).toBe('day');
  });
});

describe('planSearch', () => {
  it('round-trips every location through the query string, under either default', () => {
    for (const def of ['week', 'day'] as PlanView[]) {
      for (const loc of [
        { view: 'day', date: '2026-11-26' },
        { view: 'day', date: TODAY },
        { view: 'week', date: '2026-11-26' },
        { view: 'week', date: TODAY },
      ] as const) {
        const search = planSearch(loc).replace(/^\?/, '');
        expect(parsePlanLocation(q(search), TODAY, def)).toEqual(loc);
      }
    }
  });

  it('always names view and date, even for today in the default view', () => {
    expect(planSearch({ view: 'week', date: TODAY })).toBe(`?view=week&date=${TODAY}`);
    expect(planSearch({ view: 'day', date: TODAY })).toBe(`?view=day&date=${TODAY}`);
    expect(planSearch({ view: 'week', date: '2026-11-26' })).toBe('?view=week&date=2026-11-26');
  });

  it('a link made on a phone for today opens the same day view on a desktop tomorrow', () => {
    // Land review blocker: phone Week → "Open in day view" for today wrote bare /meal-plan, which a desktop
    // opened as Week, and a reload the next day moved to that day.
    const link = planSearch({ view: 'day', date: TODAY }).replace(/^\?/, '');
    expect(parsePlanLocation(q(link), '2026-11-27', 'week')).toEqual({ view: 'day', date: TODAY });
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
