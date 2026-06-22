// ─────────────────────────────────────────────────────────────────────────
// Tiny date helpers for the island. The board NEVER does dueness/decay date
// math client-side (MN4) — `dueState`/`colorTier`/`isSnoozed` are all server-
// computed. The one thing the client legitimately needs is a LOWER BOUND for
// the snooze/first-due date pickers, so a user can't pick today/past in a field
// that means "defer to the future". The server is the authority (it rejects a
// non-future floor — ChoresEndpoints.ValidateFloor); this only bounds the picker.
// ─────────────────────────────────────────────────────────────────────────

/**
 * Local today + 1, formatted "YYYY-MM-DD", for a date input's `min`. Built from
 * LOCAL date PARTS — never `new Date('YYYY-MM-DD')` and never a UTC ISO slice
 * (`toISOString().slice(0,10)`), either of which shifts the day backward in a
 * US timezone. A cross-tz member could still see the household's day boundary
 * differ by one, but the picker bound is advisory only — the server validates.
 */
export function minFloorDate(): string {
  const d = new Date();
  d.setDate(d.getDate() + 1);
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}
