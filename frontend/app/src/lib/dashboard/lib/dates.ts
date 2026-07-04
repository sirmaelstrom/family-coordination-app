// ─────────────────────────────────────────────────────────────────────────
// Tiny display-only date formatters for the dashboard's two labels (the
// welcome banner's long date + the meals card's short date). The dashboard has
// NO week math (unlike meal-plan) — it only formats the server-echoed `today`.
//
// ⚠ MN4 / global CORRECTION: never `new Date("YYYY-MM-DD")` — that parses as
//   UTC midnight, which renders the PREVIOUS day in US timezones. We parse the
//   components and build the date at NOON-UTC, which is the same calendar day in
//   every timezone, then format with the user's locale.
// ─────────────────────────────────────────────────────────────────────────

/** Parse a "YYYY-MM-DD" into a Date pinned at noon UTC (tz-stable calendar day). */
function parseIsoNoon(iso: string): Date {
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d, 12));
}

/** "Wednesday, June 24" — the welcome-banner date (parity with Home.razor "dddd, MMMM d"). */
export function formatLongDate(iso: string): string {
  return parseIsoNoon(iso).toLocaleDateString(undefined, {
    weekday: 'long',
    month: 'long',
    day: 'numeric',
    timeZone: 'UTC',
  });
}

/** "Jun 24" — the meals-card date (parity with Home.razor "MMM d"). */
export function formatShortDate(iso: string): string {
  return parseIsoNoon(iso).toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  });
}
