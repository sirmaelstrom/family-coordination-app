/**
 * Parsing for the per-entry servings override.
 *
 * Pure and separate from the component because the interesting half is the EMPTY case: blank means
 * "cook it as the recipe is written", which is a real answer rather than an abandoned one, and it has to
 * reach the API as `null`. The dialog blocks empty submissions unless a caller opts in (`allowEmpty`), so
 * the two halves have to agree — this is the half a test can reach.
 */
export type ServingsParse =
  | { ok: true; servings: number | null }
  | { ok: false; message: string };

export function parseServingsInput(raw: string): ServingsParse {
  const trimmed = raw.trim();
  if (trimmed === '') return { ok: true, servings: null };

  // Number() accepts '1e3', ' 12 ' and '0x10'; Number.isInteger rejects the fractional results and the
  // guard below rejects the rest. The server validates independently — this is for a fast, local message.
  const parsed = Number(trimmed);
  if (!Number.isFinite(parsed) || !Number.isInteger(parsed)) {
    return { ok: false, message: 'Enter a whole number of servings, or leave it blank.' };
  }
  if (parsed < 1) {
    return { ok: false, message: 'Servings must be at least 1, or leave it blank to cook it as written.' };
  }
  if (parsed > MAX_SERVINGS) {
    return { ok: false, message: `That is more than ${MAX_SERVINGS} servings — check the number.` };
  }
  return { ok: true, servings: parsed };
}

/**
 * The servings chip on a meal card, or null for no chip.
 *
 * A recipe with no yield gets no chip to START an override (the override would scale nothing), but a
 * SAVED override always shows, yield or not: the yield can be cleared after the override was set, and
 * hiding a saved value would leave it unseen and unclearable.
 */
export function servingsChip(
  recipeServings: number | null | undefined,
  override: number | null | undefined,
): { text: string; label: string } | null {
  const hasYield = recipeServings != null && recipeServings > 0;
  if (override != null) {
    return {
      text: `for ${override}`,
      label: hasYield
        ? `Cooking for ${override}; recipe serves ${recipeServings}. Change`
        : `Cooking for ${override}; the recipe has no yield set. Change`,
    };
  }
  if (hasYield) {
    return { text: `serves ${recipeServings}`, label: `Recipe serves ${recipeServings}. Cook for a different number` };
  }
  return null;
}

/** Mirrors the server's cap (MealPlanService.MaxServings). Both sides validate; neither trusts the other. */
export const MAX_SERVINGS = 1000;
