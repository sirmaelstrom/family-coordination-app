// Tiny date helper for the settings island. The only dates on the wire are the
// deletedAt of a category and of a recipe — a FULL ISO-8601 instant (UTC), review X5. We parse the
// full instant (new Date(iso) handles the offset correctly) and format it in the
// user's local timezone. This is NOT the noon-UTC date-only case (that trick is
// only for bare "YYYY-MM-DD" strings); never use new Date('YYYY-MM-DD') here.

/**
 * "deleted Jun 20"-style short date for a deleted category or recipe, or "" when absent/invalid.
 * A date from another year carries its year ("Jun 20, 2025"): deleted rows are kept for good, so
 * a bare "Jun 20" stops saying which June.
 */
export function formatDeletedDate(iso: string | null, now: Date = new Date()): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const sameYear = d.getFullYear() === now.getFullYear();
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', ...(sameYear ? {} : { year: 'numeric' }) });
}
