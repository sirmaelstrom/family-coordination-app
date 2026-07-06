// ─────────────────────────────────────────────────────────────────────────
// Capacity-fit — the pure logic behind the up-for-grabs "Fits me" affordance
// (Phase 15 R4′ WP-03). Deliberately RUNE-FREE and dependency-free (only type
// imports) so it is trivially unit-testable in isolation (V1.6) and so the store
// + components share ONE source of truth for the whitelist gate + the fit sets.
//
// The ONLY inputs anywhere here are the chore's declared weight (effortTier) and
// the viewer's OWN self-set capacity tier. There is NO per-person share/history/
// gap signal — that is the whole point (M1/M6/MN1).
// ─────────────────────────────────────────────────────────────────────────

import type { CapacityTier, EffortTier } from './types';

/**
 * Does the "Fits me" chip render for this caller's capacity tier? WHITELIST gate (V1.3): the chip shows IFF
 * the tier is `Reduced` or `Minimal`. Both `'Full'` AND `null`/`undefined` (unset ⇒ Full) hide it — that is
 * the founding-case guarantee (a Full/unset viewer, e.g. Justin, is never shown a per-person affordance).
 *
 * NEVER express this as `tier !== 'Full'` — that would leak the chip to a null-tier user, breaking the one
 * invariant the whole feature exists to protect.
 */
export function showsFitsMe(tier: CapacityTier | null | undefined): boolean {
  return tier === 'Reduced' || tier === 'Minimal';
}

/**
 * The effort tiers that FIT a given capacity tier. `Minimal` → Quick only; `Reduced` → Quick + Standard;
 * `Full`/unset → everything (the chip is hidden for these, so this branch is a no-op safety net). Returned in
 * ascending-weight order for readability; callers use it as a membership set.
 */
export function fitSetFor(tier: CapacityTier | null | undefined): readonly EffortTier[] {
  switch (tier) {
    case 'Minimal':
      return ['Quick'];
    case 'Reduced':
      return ['Quick', 'Standard'];
    default:
      // Full / null / undefined — everything fits (no filtering).
      return ['Quick', 'Standard', 'BigJob'];
  }
}

/** Does a chore's declared effort tier fit the viewer's own capacity tier? */
export function fitsCapacity(effortTier: EffortTier, tier: CapacityTier | null | undefined): boolean {
  return fitSetFor(tier).includes(effortTier);
}
