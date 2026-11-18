// ─────────────────────────────────────────────────────────────────────────
// Where the meal plan is looking: a view (week | day) and an anchor date. The
// URL is the only home of this state (`/meal-plan?view=day&date=2026-11-26`),
// so a day is linkable and survives reload. Rune-free on purpose so vitest
// (plain node) covers the parse/serialize round trip.
//
// The anchor is always a DAY, in both views. Week view shows the week that
// contains it; stepping a week moves it by 7 days, so Week → Day lands on the
// same weekday you were looking at, and Day → Week → Day comes back to the same
// day. Plain `/meal-plan` (the nav link) is today in the screen's default
// view: week on a desktop, day on a phone (the phone week list squeezes names
// to a few characters). Every navigation inside the page names its view and
// date, so a copied link opens the same view and day on every device, any day.
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
 * back to `today`; a missing or unknown `view` falls back to `defaultView`.
 */
export function parsePlanLocation(
  params: Pick<URLSearchParams, 'get'>,
  today: string,
  defaultView: PlanView,
): PlanLocation {
  const raw = params.get('date');
  const view = params.get('view');
  return {
    view: view === 'day' || view === 'week' ? view : defaultView,
    date: isDateStr(raw) ? raw : today,
  };
}

/**
 * The query string for a location the user navigated to. It always names both
 * view and date, even when they match this screen's default and today: the
 * bare `/meal-plan` means "today, in whatever view this screen defaults to",
 * so a link copied from a phone's day view for today would open Week on a
 * desktop, and another day tomorrow. Only the nav link is bare.
 */
export function planSearch(loc: PlanLocation): string {
  return `?view=${loc.view}&date=${loc.date}`;
}

/** One step back (-1) or forward (+1): a day in day view, a week in week view. */
export function stepPlan(loc: PlanLocation, dir: -1 | 1): PlanLocation {
  return { view: loc.view, date: addDays(loc.date, loc.view === 'day' ? dir : dir * 7) };
}

/**
 * True only when the board in hand is the week being shown. Moving to another
 * week updates `weekStart` at once and keeps the old board until the new one
 * arrives (or fails), and the old board must not stand in for the new week.
 */
export function boardIsForWeek(board: { weekStartDate: string } | null, weekStart: string): boolean {
  return board != null && board.weekStartDate === weekStart;
}

/** The Monday of the week the store must hold to render this location. */
export function planWeek(loc: PlanLocation): string {
  return mondayOf(loc.date);
}
