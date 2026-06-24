// ─────────────────────────────────────────────────────────────────────────
// Week helpers for the meal-plan island.
//
// ⚠ GLOBAL PROJECT RULE / MN4: NEVER `new Date('YYYY-MM-DD')`. The string form
// parses as UTC midnight, which renders as the PREVIOUS day in US timezones —
// a wrong-week bug. We instead:
//   • parse a "YYYY-MM-DD" by SPLITTING on '-' into integer parts, and
//   • build dates with `Date.UTC(y, m, d, 12)` — NOON UTC, far enough from
//     either midnight that no DST shift can cross a day boundary — then read
//     the UTC parts back. Stepping by whole weeks is exact this way.
//
// All of this is DISPLAY-ONLY: the server re-snaps `weekStart` to that week's
// Monday on every board GET, so client stepping can never corrupt the boundary.
// ─────────────────────────────────────────────────────────────────────────

const WEEKDAY_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const MONTH_SHORT = [
  'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

/** Parse a "YYYY-MM-DD" string into [year, month(1-12), day] integers. No Date built. */
function parseParts(dateStr: string): [number, number, number] {
  const [y, m, d] = dateStr.split('-').map((s) => Number(s));
  return [y, m, d];
}

/** A noon-UTC Date for "YYYY-MM-DD" — DST-safe for whole-day/-week math only. */
function utcNoon(dateStr: string): Date {
  const [y, m, d] = parseParts(dateStr);
  return new Date(Date.UTC(y, m - 1, d, 12));
}

/** Format a noon-UTC Date back to "YYYY-MM-DD" (reads UTC parts — never local). */
function formatUtc(date: Date): string {
  const y = date.getUTCFullYear();
  const m = String(date.getUTCMonth() + 1).padStart(2, '0');
  const d = String(date.getUTCDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

/**
 * The Monday of the week containing `dateStr` ("YYYY-MM-DD"). Mirrors the
 * server's GetWeekStartDate: `diff = (7 + (dow - Monday)) % 7`, then subtract.
 * The server re-snaps anyway; this keeps the client label correct between GETs.
 */
export function mondayOf(dateStr: string): string {
  const d = utcNoon(dateStr);
  // getUTCDay(): Sunday=0 … Saturday=6. Monday=1.
  const diff = (7 + (d.getUTCDay() - 1)) % 7;
  d.setUTCDate(d.getUTCDate() - diff);
  return formatUtc(d);
}

/** A "YYYY-MM-DD" `n` weeks from `mondayStr` (n may be negative). */
export function addWeeks(mondayStr: string, n: number): string {
  const d = utcNoon(mondayStr);
  d.setUTCDate(d.getUTCDate() + n * 7);
  return formatUtc(d);
}

/** Monday of the LOCAL current week, as "YYYY-MM-DD". The one place we read "today". */
export function todayMonday(): string {
  const now = new Date();
  // Build today's string from LOCAL parts (the household's calendar day), then
  // snap to Monday via the UTC-noon helpers. No `new Date('YYYY-MM-DD')`.
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  return mondayOf(`${y}-${m}-${d}`);
}

/** The 7 "YYYY-MM-DD" day strings of the week starting at `mondayStr`. */
export function weekDays(mondayStr: string): string[] {
  const base = utcNoon(mondayStr);
  const out: string[] = [];
  for (let i = 0; i < 7; i++) {
    const d = new Date(base.getTime());
    d.setUTCDate(d.getUTCDate() + i);
    out.push(formatUtc(d));
  }
  return out;
}

/** Short weekday label, e.g. "Mon" (for a "YYYY-MM-DD"). */
export function weekdayShort(dateStr: string): string {
  return WEEKDAY_SHORT[utcNoon(dateStr).getUTCDay()];
}

/** Full weekday label, e.g. "Monday". */
export function weekdayLong(dateStr: string): string {
  const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
  return days[utcNoon(dateStr).getUTCDay()];
}

/** "MMM d" label, e.g. "Jun 23". */
export function monthDay(dateStr: string): string {
  const d = utcNoon(dateStr);
  return `${MONTH_SHORT[d.getUTCMonth()]} ${d.getUTCDate()}`;
}

/** Full day label, e.g. "Monday, June 23". */
export function dayLong(dateStr: string): string {
  const months = [
    'January', 'February', 'March', 'April', 'May', 'June',
    'July', 'August', 'September', 'October', 'November', 'December',
  ];
  const d = utcNoon(dateStr);
  return `${weekdayLong(dateStr)}, ${months[d.getUTCMonth()]} ${d.getUTCDate()}`;
}

/**
 * Week-range label, e.g. "Jun 23 – Jun 29, 2025" (matches the Blazor
 * MealPlanNavigation's "MMM d - MMM d, yyyy"). `mondayStr` is the week's Monday.
 */
export function weekRangeLabel(mondayStr: string): string {
  const endDate = utcNoon(mondayStr);
  endDate.setUTCDate(endDate.getUTCDate() + 6);
  const endStr = formatUtc(endDate);
  return `${monthDay(mondayStr)} – ${monthDay(endStr)}, ${endDate.getUTCFullYear()}`;
}

/** Is `dateStr` the local current day? (drives the "today" highlight). */
export function isToday(dateStr: string): boolean {
  const now = new Date();
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, '0');
  const d = String(now.getDate()).padStart(2, '0');
  return dateStr === `${y}-${m}-${d}`;
}
