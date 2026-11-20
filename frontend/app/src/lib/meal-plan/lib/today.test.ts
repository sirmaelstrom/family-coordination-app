import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from 'svelte/server';
import { TodayClock, msUntilNextDay } from './today.svelte';
import PlanNav from './components/PlanNav.svelte';
import type { PlanView } from './view';

// The meal plan's "today" must follow the clock while the page stays open (quest 70f237a1, from
// fca#170's review: a clock read with no reactive dependency kept yesterday's Today chip after
// midnight). Fake clock; every time is built from LOCAL parts, so the tests hold in any timezone.

const THU_2359_30 = new Date(2026, 10, 19, 23, 59, 30);

describe('TodayClock', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(THU_2359_30);
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('follows the clock across midnight, and across the next one', () => {
    const clock = new TodayClock();
    const stop = clock.start();
    expect(clock.value).toBe('2026-11-19');

    vi.advanceTimersByTime(29_000); // 23:59:59
    expect(clock.value).toBe('2026-11-19');

    vi.advanceTimersByTime(2_000); // 00:00:01 Friday
    expect(clock.value).toBe('2026-11-20');

    vi.advanceTimersByTime(24 * 60 * 60 * 1000); // 00:00:01 Saturday: the timer re-armed itself
    expect(clock.value).toBe('2026-11-21');
    stop();
  });

  it('catches up on sync() when no timer ran (a phone that slept through midnight)', () => {
    const clock = new TodayClock();
    const stop = clock.start();

    vi.setSystemTime(new Date(2026, 10, 20, 7, 0, 0)); // the clock jumps; no timer fires
    expect(clock.value).toBe('2026-11-19');

    clock.sync();
    expect(clock.value).toBe('2026-11-20');
    stop();
  });

  it('stops: no timer is left behind and the day no longer moves', () => {
    const clock = new TodayClock();
    const stop = clock.start();
    stop();
    expect(vi.getTimerCount()).toBe(0);

    vi.advanceTimersByTime(60_000);
    expect(clock.value).toBe('2026-11-19');
  });

  it('starting twice keeps one timer', () => {
    const clock = new TodayClock();
    clock.start();
    const stop = clock.start();
    expect(vi.getTimerCount()).toBe(1);
    stop();
    expect(vi.getTimerCount()).toBe(0);
  });
});

// The bare /meal-plan link's day (fca#175, design note fca-175-no-switch-2026-11-20): read from the wall
// clock once per navigation (each one hands the page a new URL object) and then kept. No clock tick, wake
// or re-read moves it, so the week on screen changes only when the member navigates or reloads. `value`
// (the Today chip, the today marks, Jump to Today) still follows the clock.
describe('TodayClock.linkDay', () => {
  const bare = () => new URL('http://fca.test/meal-plan');

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date(2026, 10, 22, 23, 59, 30)); // Sunday Nov 22
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('a clock tick past Sunday midnight leaves the bare link on Sunday; Today moves', () => {
    const clock = new TodayClock();
    const stop = clock.start();
    const arrived = bare();
    expect(clock.linkDay(arrived)).toBe('2026-11-22');

    vi.advanceTimersByTime(45_000); // 00:00:15 Monday Nov 23: the midnight timer fires

    expect(clock.value).toBe('2026-11-23'); // the Today mark and Jump to Today follow the clock
    expect(clock.linkDay(arrived)).toBe('2026-11-22'); // the week on screen does not
    stop();
  });

  it('a wake (sync) after midnight does not move it either', () => {
    const clock = new TodayClock(); // never started: a phone asleep runs no timer
    const arrived = bare();
    expect(clock.linkDay(arrived)).toBe('2026-11-22');
    vi.setSystemTime(new Date(2026, 10, 23, 7, 0)); // Monday 07:00, the tab is shown again
    clock.sync(); // what App's liveness tick does on wake
    expect(clock.value).toBe('2026-11-23');
    expect(clock.linkDay(arrived)).toBe('2026-11-22');
  });

  it('a navigation (a new page URL, even to the same address) reads the clock again', () => {
    const clock = new TodayClock();
    const stop = clock.start();
    expect(clock.linkDay(bare())).toBe('2026-11-22');
    vi.advanceTimersByTime(45_000);
    expect(clock.linkDay(bare())).toBe('2026-11-23'); // the member clicked Meal Plan, or reloaded
    stop();
  });
});

describe('the Today chip across midnight, with the view left open', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(THU_2359_30);
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  /** PlanNav as its owner draws it: the day on screen, and the clock's today. */
  function planNav(view: PlanView, date: string, today: string) {
    const noop = (): void => {};
    const html = render(PlanNav, {
      props: { view, date, today, onPrev: noop, onNext: noop, onToday: noop, onViewChange: noop },
    }).body;
    return {
      chip: /<span class="mp-nav-chip[^"]*">([^<]*)<\/span>/.exec(html)?.[1] ?? null,
      jumpDisabled: /<button[^>]*class="mp-nav-today[^"]*"[^>]*\sdisabled/.test(html),
    };
  }

  it('leaves Thursday at midnight, and Jump to Today wakes up', () => {
    const clock = new TodayClock();
    const stop = clock.start();
    expect(planNav('day', '2026-11-19', clock.value)).toEqual({ chip: 'Today', jumpDisabled: true });

    vi.advanceTimersByTime(45_000); // 00:00:15 Friday, still looking at Thursday

    expect(planNav('day', '2026-11-19', clock.value)).toEqual({ chip: null, jumpDisabled: false });
    expect(planNav('day', '2026-11-20', clock.value)).toEqual({ chip: 'Today', jumpDisabled: true });
    // Thursday and Friday share a week, so week view keeps its chip.
    expect(planNav('week', '2026-11-19', clock.value)).toEqual({ chip: 'This week', jumpDisabled: true });
    stop();
  });

  it('leaves this week at Sunday midnight', () => {
    vi.setSystemTime(new Date(2026, 10, 22, 23, 59, 30)); // Sunday Nov 22
    const clock = new TodayClock();
    const stop = clock.start();
    expect(planNav('week', '2026-11-22', clock.value)).toEqual({ chip: 'This week', jumpDisabled: true });

    vi.advanceTimersByTime(45_000); // Monday Nov 23

    expect(planNav('week', '2026-11-22', clock.value)).toEqual({ chip: null, jumpDisabled: false });
    stop();
  });
});

describe('msUntilNextDay', () => {
  it('is the time left until the next local midnight', () => {
    expect(msUntilNextDay(THU_2359_30)).toBe(30_000);
    expect(msUntilNextDay(new Date(2026, 11, 31, 23, 0, 0))).toBe(60 * 60 * 1000); // New Year's Eve
  });
});
