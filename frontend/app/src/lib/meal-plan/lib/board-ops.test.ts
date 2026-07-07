import { describe, it, expect } from 'vitest';
import {
  applyEntryMove,
  buildZones,
  dragId,
  findCrossSlotEntry,
  replaceEntry,
  zoneKey,
  type DragEntry,
} from './board-ops';
import type { MealPlanEntryDto, MealType } from './types';

// ─────────────────────────────────────────────────────────────────────────
// Drag-to-assign transforms. The store (state.svelte.ts) is a thin reactive
// shell over these pure functions — the parts unit tests can pin down are the
// optimistic move, the failure REVERT (the same transform aimed back at the
// original slot), the server-echo merge, and the zone bucketing contract
// svelte-dnd-action depends on (`id` on every row; every slot — empty ones
// included — owning its own array).
// ─────────────────────────────────────────────────────────────────────────

const WEEK = ['2026-07-06', '2026-07-07', '2026-07-08', '2026-07-09', '2026-07-10', '2026-07-11', '2026-07-12'];
const MEALS: MealType[] = ['breakfast', 'lunch', 'dinner'];

function entry(overrides: Partial<MealPlanEntryDto> = {}): MealPlanEntryDto {
  return {
    mealPlanId: 1,
    entryId: 1,
    date: '2026-07-06',
    mealType: 'dinner',
    recipe: { recipeId: 10, name: 'Tacos', imagePath: null, recipeType: 'main' },
    customMealName: null,
    notes: null,
    ...overrides,
  };
}

describe('buildZones — the per-slot dnd source', () => {
  it('seeds EVERY day × meal slot, empty ones included (empty slots must be live drop targets)', () => {
    const zones = buildZones([], WEEK, MEALS);
    expect(Object.keys(zones)).toHaveLength(21);
    expect(zones[zoneKey('2026-07-08', 'lunch')]).toEqual([]);
  });

  it('buckets entries into their slot and augments each row with the dnd `id`', () => {
    const a = entry({ entryId: 1 });
    const b = entry({ entryId: 2, mealType: 'lunch' });
    const zones = buildZones([a, b], WEEK, MEALS);

    const dinner = zones[zoneKey('2026-07-06', 'dinner')];
    expect(dinner).toHaveLength(1);
    // svelte-dnd-action hard-requires `id` — entryId alone is not unique across
    // plans, so the row id compounds both keys (the settings CategoriesApp fix).
    expect(dinner[0].id).toBe('1:1');
    expect(dragId(b)).toBe('1:2');
    expect(zones[zoneKey('2026-07-06', 'lunch')][0].entryId).toBe(2);
  });

  it('keeps an off-row entry (e.g. a data-carried snack) by creating its key', () => {
    const snack = entry({ mealType: 'snack' as MealType });
    const zones = buildZones([snack], WEEK, MEALS);
    expect(zones[zoneKey('2026-07-06', 'snack' as MealType)]).toHaveLength(1);
  });
});

describe('findCrossSlotEntry — destination-drop detection at finalize', () => {
  it('finds the dropped row by its still-original slot disagreeing with the zone', () => {
    const dragged: DragEntry = { ...entry(), id: '1:1' }; // still says Mon|dinner
    const resident: DragEntry = { ...entry({ entryId: 2, date: '2026-07-07', mealType: 'lunch' }), id: '1:2' };
    expect(findCrossSlotEntry([resident, dragged], '2026-07-07', 'lunch')).toBe(dragged);
  });

  it('returns null for the source zone / a same-slot reorder (nothing to persist)', () => {
    const a: DragEntry = { ...entry({ entryId: 1 }), id: '1:1' };
    const b: DragEntry = { ...entry({ entryId: 2 }), id: '1:2' };
    expect(findCrossSlotEntry([b, a], '2026-07-06', 'dinner')).toBeNull();
    expect(findCrossSlotEntry([], '2026-07-06', 'dinner')).toBeNull();
  });
});

describe('applyEntryMove — optimistic move + failure revert', () => {
  const others = [entry({ entryId: 2 }), entry({ entryId: 3, mealType: 'breakfast' })];
  const moving = entry({ entryId: 1 });

  it('re-slots exactly the identified entry, immutably', () => {
    const before = [...others, moving];
    const after = applyEntryMove(before, 1, 1, '2026-07-09', 'lunch');

    const movedEntry = after.find((e) => e.entryId === 1)!;
    expect(movedEntry.date).toBe('2026-07-09');
    expect(movedEntry.mealType).toBe('lunch');
    // Untouched entries keep identity; the source array and object are not mutated.
    expect(after.find((e) => e.entryId === 2)).toBe(others[0]);
    expect(moving.date).toBe('2026-07-06');
    expect(before).toHaveLength(3);
  });

  it('REVERT: aiming the transform back at the original slot restores the pre-move board', () => {
    const before = [...others, moving];
    const optimistic = applyEntryMove(before, 1, 1, '2026-07-09', 'lunch');
    // Network/5xx failure path — the store replays the move toward the captured original slot.
    const reverted = applyEntryMove(optimistic, 1, 1, moving.date, moving.mealType);
    expect(reverted).toEqual(before);
  });

  it('is a no-op for an entry that vanished (removed by someone else mid-flight)', () => {
    const after = applyEntryMove(others, 1, 99, '2026-07-09', 'lunch');
    expect(after).toEqual(others);
  });

  it('matches on BOTH mealPlanId and entryId (entryId alone is not identity)', () => {
    const foreign = entry({ mealPlanId: 2, entryId: 1 });
    const after = applyEntryMove([foreign], 1, 1, '2026-07-09', 'lunch');
    expect(after[0]).toBe(foreign);
  });
});

describe('replaceEntry — merging the server echo after a successful move', () => {
  it('swaps in the authoritative entry by identity, leaving the rest alone', () => {
    const stale = entry({ entryId: 1, date: '2026-07-09', mealType: 'lunch' });
    const other = entry({ entryId: 2 });
    const echo = entry({ entryId: 1, date: '2026-07-09', mealType: 'lunch', notes: 'server-stamped' });

    const after = replaceEntry([stale, other], echo);
    expect(after.find((e) => e.entryId === 1)).toBe(echo);
    expect(after.find((e) => e.entryId === 2)).toBe(other);
  });

  it('drops the echo when the entry is gone (a liveness refresh already removed it)', () => {
    const other = entry({ entryId: 2 });
    const echo = entry({ entryId: 1 });
    expect(replaceEntry([other], echo)).toEqual([other]);
  });
});
