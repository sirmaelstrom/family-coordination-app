// Date helpers for the admin island. Every date on the wire (requestedAt /
// reviewedAt / createdAt) is a FULL ISO-8601 instant WITH time-of-day (UTC,
// review X5) — NOT the noon-UTC date-only case. We parse the full instant
// (new Date(iso) handles the offset correctly) and format it in the user's
// local timezone. Never use new Date('YYYY-MM-DD') here.

/** Full local date+time ("Jun 24, 2026, 1:30 PM"), or "" when absent/invalid. Parity: ToLocalTime + date/time. */
export function formatDateTime(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleString();
}

/** Local date only ("Jun 24, 2026"), or "" when absent/invalid (the reviewed/created short form). */
export function formatDate(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}
