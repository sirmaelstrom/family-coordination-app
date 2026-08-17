// ─────────────────────────────────────────────────────────────────────────
// Import-flow logic — the pure state machine behind ImportDialog (quest 1a8b49ea,
// the capacity-fit pattern: rune-free, dependency-light, unit-testable). The
// dialog holds ONE `ImportOutcome` and projects its old booleans from it; the
// response→outcome ladder, the preview→WriteRequest mapping and the render
// model all live here, in one place each.
// ─────────────────────────────────────────────────────────────────────────
import type {
  PartialRecipeDataDto,
  RecipeImportPreviewDto,
  RecipePreviewDto,
  RecipeWriteRequest,
} from './types';
import { formatExactQuantity } from './quantity';

/** The six-way result of a preview attempt, as one discriminated union. */
export type ImportOutcome =
  | { kind: 'preview'; preview: RecipePreviewDto }
  | { kind: 'partial'; partial: PartialRecipeDataDto; warning: string }
  | { kind: 'duplicate'; existingRecipeId: number; message: string }
  | { kind: 'error'; message: string; offerManual: boolean };

/** Decode the preview response into an outcome — the one decision ladder. */
export function decodePreview(res: RecipeImportPreviewDto): ImportOutcome {
  if (res.success && res.recipe != null) {
    return { kind: 'preview', preview: res.recipe };
  }
  if (res.existingRecipeId != null) {
    return {
      kind: 'duplicate',
      existingRecipeId: res.existingRecipeId,
      message: res.errorMessage ?? 'This recipe has already been imported.',
    };
  }
  if (res.partialData != null) {
    // Failure honesty: show what DID come back before the user decides.
    return {
      kind: 'partial',
      partial: res.partialData,
      warning: res.errorMessage ?? 'Only part of the recipe could be extracted.',
    };
  }
  return {
    kind: 'error',
    message: res.errorMessage ?? 'Import failed for unknown reason.',
    // Offer manual entry unless the URL itself was invalid.
    offerManual: res.errorType !== 'InvalidUrl',
  };
}

/**
 * The confirm payload for an outcome, or null when nothing confirmable is on screen.
 * A full preview posts VERBATIM (+ version: null — a CREATE has no xmin token), which is
 * the "preview and confirm produce the same recipe" contract; a partial parse maps its
 * degraded fields (unparsed ingredient strings become name-only Pantry lines) and needs
 * at least a title.
 */
export function toWriteRequest(outcome: ImportOutcome | null, url: string): RecipeWriteRequest | null {
  if (outcome?.kind === 'preview') {
    return { ...outcome.preview, version: null };
  }
  if (outcome?.kind === 'partial' && outcome.partial.name?.trim()) {
    const partial = outcome.partial;
    return {
      version: null,
      name: partial.name!.trim(),
      description: partial.description,
      instructions: partial.instructions,
      sourceUrl: url.trim(),
      servings: partial.servings,
      prepTimeMinutes: partial.prepTimeMinutes,
      cookTimeMinutes: partial.cookTimeMinutes,
      recipeType: 'main',
      imagePath: partial.imageUrl,
      ingredients: (partial.ingredientStrings ?? []).map((s, i) => ({
        name: s,
        quantity: null,
        unit: null,
        category: 'Pantry',
        notes: null,
        groupName: null,
        sortOrder: i,
      })),
    };
  }
  return null;
}

/** One render model for both full and partial previews (null when neither is on screen). */
export interface PreviewViewModel {
  name: string | null;
  description: string | null;
  instructions: string | null;
  imageUrl: string | null;
  sourceUrl: string;
  servings: number | null;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  ingredients: string[];
}

export function previewViewModel(outcome: ImportOutcome | null, url: string): PreviewViewModel | null {
  if (outcome?.kind === 'preview') {
    const preview = outcome.preview;
    return {
      name: preview.name as string | null,
      description: preview.description,
      instructions: preview.instructions,
      imageUrl: preview.imagePath,
      sourceUrl: preview.sourceUrl ?? url.trim(),
      servings: preview.servings,
      prepTimeMinutes: preview.prepTimeMinutes,
      cookTimeMinutes: preview.cookTimeMinutes,
      ingredients: preview.ingredients.map((i) => {
        const qty = i.quantity != null ? formatExactQuantity(i.quantity) : null;
        const line = [qty, i.unit, i.name].filter(Boolean).join(' ');
        return i.notes ? `${line} (${i.notes})` : line;
      }),
    };
  }
  if (outcome?.kind === 'partial') {
    const partial = outcome.partial;
    return {
      name: partial.name,
      description: partial.description,
      instructions: partial.instructions,
      imageUrl: partial.imageUrl,
      sourceUrl: url.trim(),
      servings: partial.servings,
      prepTimeMinutes: partial.prepTimeMinutes,
      cookTimeMinutes: partial.cookTimeMinutes,
      ingredients: partial.ingredientStrings ?? [],
    };
  }
  return null;
}
