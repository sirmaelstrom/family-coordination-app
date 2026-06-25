// ─────────────────────────────────────────────────────────────────────────
// Date helpers for the connections island. Both dates on the wire (invite
// expiresAt, connected connectedAt) are FULL ISO-8601 instants (UTC) — review X5.
// We parse the full instant (new Date(iso) handles the offset correctly) and
// format in the user's local timezone. This is NOT the noon-UTC date-only case
// (that trick is only for bare "YYYY-MM-DD"); never new Date('YYYY-MM-DD') here.
// ─────────────────────────────────────────────────────────────────────────

/**
 * Relative-until-or-absolute expiry text — a faithful port of Connections.razor's
 * GetExpiryText (:350-357). Rendered as "Expires {this}" in the share card:
 *   < 1 min   → "in less than a minute"
 *   < 60 min  → "in N minutes"
 *   < 24 hr   → "in N hours"
 *   else      → "on Mon D, YYYY" (local)
 * Integer truncation matches the C# (int) cast.
 */
export function formatExpiry(iso: string): string {
  const expires = new Date(iso);
  if (Number.isNaN(expires.getTime())) return '';
  const remainingMs = expires.getTime() - Date.now();
  const minutes = remainingMs / 60_000;
  if (minutes < 1) return 'in less than a minute';
  if (minutes < 60) return `in ${Math.floor(minutes)} minutes`;
  const hours = remainingMs / 3_600_000;
  if (hours < 24) return `in ${Math.floor(hours)} hours`;
  return `on ${expires.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })}`;
}

/** "Jun 20, 2026" connected-date — parity Connections.razor:173 ("MMM d, yyyy" local), or "" when invalid. */
export function formatConnectedDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}
