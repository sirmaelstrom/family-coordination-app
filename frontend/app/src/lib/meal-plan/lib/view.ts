// ─────────────────────────────────────────────────────────────────────────
// Where the meal plan is looking: a view (week | day) and an anchor date. The
// URL is the only home of this state (`/meal-plan?view=day&date=2026-11-26`),
// so a day is linkable and survives reload. Rune-free on purpose so vitest
// (plain node) covers the parse/serialize round trip.
//
// The anchor is always a DAY, in both views. Week view shows the week that
// contains it; stepping a week moves it by 7 days, so Week → Day lands on the
// same weekday you were looking at, and Day → Week → Day comes back to the same
// day. Plain `/meal-plan` is week view anchored on today.
//
// No `new Date('YYYY-MM-DD')` (MN4): dates are validated and stepped through
// lib/dates.ts.
// ─────────────────────────────────────────────────────────────────────────

import { addDays, isDateStr, mondayOf } from './dates';

export type PlanView = 'week' | 'day';

export interface PlanLocation {
  view: PlanView;
  /** "YYYY-MM-DD" — the day shown (day view) or a day inside the week shown (week view). */
  date: string;
}

/**
 * Read the location from the query string. A missing or invalid `date` falls
 * back to `today`; anything but `view=day` is week view (the default).
 */
export function parsePlanLocation(params: URLSearchParams, today: string): PlanLocation {
  const raw = params.get('date');
  return {
    view: params.get('view') === 'day' ? 'day' : 'week',
    date: isDateStr(raw) ? raw : today,
  };
}

/**
 * The query string for a location ('' or '?…'). Week view anchored on today is
 * the bare `/meal-plan`; day view always names its date, so a shared link means
 * the same day tomorrow.
 */
export function planSearch(loc: PlanLocation, today: string): string {
  if (loc.view === 'day') return `?view=day&date=${loc.date}`;
  return loc.date === today ? '' : `?date=${loc.date}`;
}

/** One step back (-1) or forward (+1): a day in day view, a week in week view. */
export function stepPlan(loc: PlanLocation, dir: -1 | 1): PlanLocation {
  return { view: loc.view, date: addDays(loc.date, loc.view === 'day' ? dir : dir * 7) };
}

/** The Monday of the week the store must hold to render this location. */
export function planWeek(loc: PlanLocation): string {
  return mondayOf(loc.date);
}
