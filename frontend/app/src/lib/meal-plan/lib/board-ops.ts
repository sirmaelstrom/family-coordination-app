// ─────────────────────────────────────────────────────────────────────────
// Pure board transforms for drag-to-assign. Rune-free on purpose — the vitest
// harness (vitest.config.ts) runs plain `node` with no Svelte plugin, so the
// dnd/move logic that needs unit coverage lives HERE and the store
// (state.svelte.ts) stays a thin reactive shell over these functions.
//
// svelte-dnd-action contracts encoded here (all three have bitten this repo):
//   • every dnd item MUST carry an `id` — MealPlanEntryDto keys on
//     (mealPlanId, entryId), so we drag over a `DragEntry` row that ADDS
//     `id: "<mealPlanId>:<entryId>"` (the settings CategoriesApp pattern).
//   • every slot is its OWN zone with its OWN array — `buildZones` seeds ALL
//     day × meal keys (empty arrays included) so an empty slot is a valid
//     drop target, and the store only ever swaps ONE zone's array mid-drag.
//   • the dragged row keeps its ORIGINAL date/mealType until finalize —
//     `findCrossSlotEntry` uses exactly that mismatch to detect a cross-slot
//     drop in the destination zone.
// ─────────────────────────────────────────────────────────────────────────

import type { MealPlanEntryDto, MealType } from './types';

/** A board entry augmented with the `id` svelte-dnd-action requires. */
export type DragEntry = MealPlanEntryDto & { id: string };

/** The dnd id — entryId alone is only unique within a plan, so compound both keys. */
export function dragId(e: Pick<MealPlanEntryDto, 'mealPlanId' | 'entryId'>): string {
  return `${e.mealPlanId}:${e.entryId}`;
}

/** One slot's zone key (`"YYYY-MM-DD|mealType"`). */
export function zoneKey(date: string, mealType: MealType): string {
  return `${date}|${mealType}`;
}

/**
 * Bucket the board's entries into per-slot drag rows. EVERY rendered slot
 * (each day × each meal row) gets a key — an empty slot owns an empty array so
 * it is a live drop target. Entries outside `mealRows` (e.g. a data-carried
 * snack) still get their own key so they are never dropped from the map.
 */
export function buildZones(
  entries: MealPlanEntryDto[],
  days: string[],
  mealRows: MealType[],
): Record<string, DragEntry[]> {
  const zones: Record<string, DragEntry[]> = {};
  for (const day of days) {
    for (const meal of mealRows) zones[zoneKey(day, meal)] = [];
  }
  for (const e of entries) {
    const key = zoneKey(e.date, e.mealType);
    (zones[key] ??= []).push({ ...e, id: dragId(e) });
  }
  return zones;
}

/**
 * The entry a finalize event moved INTO this zone: its (still-original)
 * date/mealType disagrees with the zone's slot. Only the destination zone's
 * items can contain one — the source zone's finalize (and any same-slot
 * reorder) yields null.
 */
export function findCrossSlotEntry(
  items: DragEntry[],
  date: string,
  mealType: MealType,
): DragEntry | null {
  return items.find((e) => e.date !== date || e.mealType !== mealType) ?? null;
}

/**
 * Re-slot one entry (optimistic move / failure revert — the revert is just the
 * same transform aimed back at the original slot). Immutable: a new array with
 * a new object for the moved entry; everything else is untouched. A missing
 * entry (already removed by someone else) is a no-op.
 */
export function applyEntryMove(
  entries: MealPlanEntryDto[],
  mealPlanId: number,
  entryId: number,
  date: string,
  mealType: MealType,
): MealPlanEntryDto[] {
  return entries.map((e) =>
    e.mealPlanId === mealPlanId && e.entryId === entryId ? { ...e, date, mealType } : e,
  );
}

/** Merge the server's authoritative echo over the optimistic entry (by identity). */
export function replaceEntry(
  entries: MealPlanEntryDto[],
  updated: MealPlanEntryDto,
): MealPlanEntryDto[] {
  return entries.map((e) =>
    e.mealPlanId === updated.mealPlanId && e.entryId === updated.entryId ? updated : e,
  );
}
