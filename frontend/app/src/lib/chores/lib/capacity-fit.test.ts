import { describe, it, expect } from 'vitest';
import { showsFitsMe, fitSetFor, fitsCapacity } from './capacity-fit';
import ladder from '../../../../../../tests/FamilyCoordinationApp.Tests/Fixtures/ChoreCapacity/capacity-ladder.json';
import type { CapacityTier } from './types';

// ─────────────────────────────────────────────────────────────────────────
// V1.6 — the ONE automatable proof of the R4′ founding-case guarantee. The
// multi-identity visual proof (V1.3) stays a manual browser observation; this
// pins the whitelist gate at the unit level so the forbidden `tier !== 'Full'`
// regression can never leak the "Fits me" chip to a null-tier (Full/unset) user.
// ─────────────────────────────────────────────────────────────────────────

describe('showsFitsMe — the whitelist chip gate (V1.6)', () => {
  it('shows the chip ONLY for Reduced and Minimal', () => {
    expect(showsFitsMe('Reduced')).toBe(true);
    expect(showsFitsMe('Minimal')).toBe(true);
  });

  it('HIDES the chip for Full AND for null/undefined (the founding-case guarantee)', () => {
    // Both must hide — a Full/unset viewer (e.g. Justin) is never shown a per-person affordance.
    expect(showsFitsMe('Full')).toBe(false);
    expect(showsFitsMe(null)).toBe(false);
    expect(showsFitsMe(undefined)).toBe(false);
  });
});

describe('fitSetFor — the per-tier fit sets', () => {
  it('Minimal → Quick only; Reduced → Quick + Standard; Full/unset → everything', () => {
    expect(fitSetFor('Minimal')).toEqual(['Quick']);
    expect(fitSetFor('Reduced')).toEqual(['Quick', 'Standard']);
    expect(fitSetFor('Full')).toEqual(['Quick', 'Standard', 'BigJob']);
    expect(fitSetFor(null)).toEqual(['Quick', 'Standard', 'BigJob']);
    expect(fitSetFor(undefined)).toEqual(['Quick', 'Standard', 'BigJob']);
  });
});

describe('fitsCapacity — membership against the fit set', () => {
  it('a Minimal viewer fits only Quick', () => {
    expect(fitsCapacity('Quick', 'Minimal')).toBe(true);
    expect(fitsCapacity('Standard', 'Minimal')).toBe(false);
    expect(fitsCapacity('BigJob', 'Minimal')).toBe(false);
  });

  it('a Reduced viewer fits Quick + Standard, not BigJob', () => {
    expect(fitsCapacity('Quick', 'Reduced')).toBe(true);
    expect(fitsCapacity('Standard', 'Reduced')).toBe(true);
    expect(fitsCapacity('BigJob', 'Reduced')).toBe(false);
  });

  it('a Full/unset viewer fits everything (filter is a no-op)', () => {
    expect(fitsCapacity('BigJob', 'Full')).toBe(true);
    expect(fitsCapacity('BigJob', null)).toBe(true);
  });
});

describe('the capacity-ladder pin (quest e7f8be86)', () => {
  // This module is the MIRROR of the server's ChoreCapacity; the fixture is byte-compared to that
  // module's own output on the C# side (ChoreCapacityTests), so these list-equality checks close
  // the loop: a doctrine change on either side fails CI until both move together.
  it('showsFitsMe matches the pinned ladder for every tier row', () => {
    const rows: [string, CapacityTier | null][] = [
      ['Full', 'Full'],
      ['Reduced', 'Reduced'],
      ['Minimal', 'Minimal'],
      ['unset', null],
    ];
    for (const [key, tier] of rows) {
      expect(showsFitsMe(tier), key).toBe(ladder.showsFitsMe[key as keyof typeof ladder.showsFitsMe]);
    }
  });

  it('fit sets match the pinned ladder exactly', () => {
    expect([...fitSetFor('Full')]).toEqual(ladder.fitSets.Full);
    expect([...fitSetFor('Reduced')]).toEqual(ladder.fitSets.Reduced);
    expect([...fitSetFor('Minimal')]).toEqual(ladder.fitSets.Minimal);
    expect([...fitSetFor(null)]).toEqual(ladder.fitSets.unset);
  });
});
