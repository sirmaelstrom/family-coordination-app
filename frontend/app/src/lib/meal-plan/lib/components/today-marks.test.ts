import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from 'svelte/server';

// Every "today" mark in the meal plan (PlanNav's Today / This week chip and Jump to Today, the week
// grid's highlighted column, the phone week list's Today panel) takes the day from its owner's
// TodayClock. None reads the clock itself: a clock read inside a component has no reactive dependency,
// so it kept yesterday's marks after midnight (quest 70f237a1, from fca#170's review).
//
// Server renders of the real components, under a fake clock that DISAGREES with the owner: a mark
// that follows the wall clock here is a mark that would go stale. (The clock itself, and the chip
// crossing midnight with it, are in ../today.test.ts.)

import PlanNav from './PlanNav.svelte';
import CalendarGrid from './CalendarGrid.svelte';
import DayList from './DayList.svelte';
import { mealPlanStore as store } from '../state.svelte';
import type { PlanView } from '../view';

const THU = '2026-11-19';
const FRI = '2026-11-20';
const WEEK = '2026-11-16'; // Mon Nov 16 – Sun Nov 22
const noop = (): void => {};

function planNav(view: PlanView, date: string, today: string) {
  const html = render(PlanNav, {
    props: { view, date, today, onPrev: noop, onNext: noop, onToday: noop, onViewChange: noop },
  }).body;
  return {
    chip: /<span class="mp-nav-chip[^"]*">([^<]*)<\/span>/.exec(html)?.[1] ?? null,
    jumpDisabled: /<button[^>]*class="mp-nav-today[^"]*"[^>]*\sdisabled/.test(html),
  };
}

const weekHandlers = {
  onSlotAdd: noop,
  onRemove: noop,
  onSetServings: noop,
  onViewRecipe: noop,
  onViewCustom: noop,
  onOpenDay: noop,
};
/** The day headers the desktop week grid marks as today, by their "Open … in day view" label. */
function gridToday(today: string): string[] {
  const html = render(CalendarGrid, { props: { weekStart: WEEK, today, ...weekHandlers } }).body;
  return [...html.matchAll(/<button[^>]*class="mp-day-header[^"]*\btoday\b[^"]*"[^>]*aria-label="Open ([^"]*) in day view"/g)].map(
    (m) => m[1],
  );
}
/** The phone week list's panels, as { date, today mark, open, Today chip }. */
function listPanels(today: string) {
  const html = render(DayList, { props: { weekStart: WEEK, today, ...weekHandlers } }).body;
  return html
    .split('<details')
    .slice(1)
    .map((panel) => {
      const tag = panel.slice(0, panel.indexOf('>'));
      return {
        date: /mp-day-summary-date[^"]*">([^<]*)</.exec(panel)?.[1],
        today: /class="mp-day-panel[^"]*\btoday\b/.test(tag),
        open: /\sopen\b/.test(tag),
        chip: /mp-day-summary-chip/.test(panel),
      };
    })
    .filter((p) => p.today || p.open || p.chip);
}

describe('today marks follow the owner, not a clock read of their own', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    store.weekStart = WEEK;
    store.board = { weekStartDate: WEEK, mealPlanId: 1, entries: [] };
    store.rebuildZones();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('PlanNav: has no clock of its own', () => {
    // The wall clock says Friday; the owner has not ticked yet and still says Thursday. Then it ticks.
    vi.setSystemTime(new Date(2026, 10, 20, 0, 0, 15));
    expect(planNav('day', THU, THU)).toEqual({ chip: 'Today', jumpDisabled: true });
    expect(planNav('day', THU, FRI)).toEqual({ chip: null, jumpDisabled: false });
    // A week boundary: Sunday Nov 22 is this week only until Monday Nov 23 is today.
    expect(planNav('week', '2026-11-22', '2026-11-22').chip).toBe('This week');
    expect(planNav('week', '2026-11-22', '2026-11-23')).toEqual({ chip: null, jumpDisabled: false });
  });

  it('week grid: the highlighted column is the owner\'s today', () => {
    vi.setSystemTime(new Date(2026, 10, 19, 23, 59, 30)); // the wall clock says Thursday
    expect(gridToday(THU)).toEqual(['Thursday, November 19']);
    expect(gridToday(FRI)).toEqual(['Friday, November 20']);
    expect(gridToday('2026-11-23')).toEqual([]); // today is in another week: no column marked
  });

  it('week list: the Today panel, its chip and the open panel are the owner\'s today', () => {
    vi.setSystemTime(new Date(2026, 10, 19, 23, 59, 30)); // the wall clock says Thursday
    expect(listPanels(THU)).toEqual([{ date: 'Nov 19', today: true, open: true, chip: true }]);
    expect(listPanels(FRI)).toEqual([{ date: 'Nov 20', today: true, open: true, chip: true }]);
    expect(listPanels('2026-11-23')).toEqual([]);
  });
});
