// Tiny date helper for the settings island. The only date on the wire is
// Category.deletedAt — a FULL ISO-8601 instant (UTC), review X5. We parse the
// full instant (new Date(iso) handles the offset correctly) and format it in the
// user's local timezone. This is NOT the noon-UTC date-only case (that trick is
// only for bare "YYYY-MM-DD" strings); never use new Date('YYYY-MM-DD') here.

/** "deleted Jun 20"-style short date for a deleted category, or "" when absent/invalid. */
export function formatDeletedDate(iso: string | null): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}
