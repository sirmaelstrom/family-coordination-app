// ─────────────────────────────────────────────────────────────────────────
// Recipe conflict diff (quest adfe3d57, operator answer (c): "show a diff
// first, then let the user choose"). After a stale-base 409 the editor fetches
// the latest saved recipe and shows, field by field, what differs between the
// user's form ("yours") and that version ("theirs"), before any overwrite.
//
// Pure: no store, no I/O. Values are normalized the way the save body is
// (trimmed, blank → null), so whitespace alone never reads as a change.
// ─────────────────────────────────────────────────────────────────────────

import type { RecipeFullDto, RecipeType } from './types';

/** The editable recipe fields as the form holds them (a subset of RecipeEditModel). */
export interface DiffableForm {
  name: string;
  description: string;
  instructions: string;
  sourceUrl: string;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  servings: number | null;
  recipeType: RecipeType;
  imagePath: string | null;
}

/** One ingredient as either side holds it (an IngredientRow or a RecipeIngredientFullDto). Every field the save
 *  body writes is here: keep-mine replaces the ingredients wholesale, so a field left out of the compare could
 *  be overwritten without being shown (slim-review on fca#159). */
export interface DiffableIngredient {
  quantity: number | null;
  unit: string | null;
  name: string;
  category: string;
  notes: string | null;
  groupName: string | null;
}

/** One scalar field that differs. `yours` / `theirs` are display strings ('' for empty). */
export interface FieldDiff {
  field: keyof DiffableForm;
  label: string;
  yours: string;
  theirs: string;
}

/** How the ingredient lists differ. Lines are display strings; a line repeated N times appears N times. */
export interface IngredientDiff {
  onlyYours: string[];
  onlyTheirs: string[];
  /** Same lines (as a multiset), different order. */
  reorderedOnly: boolean;
}

export interface RecipeConflictDiff {
  fields: FieldDiff[];
  /** Null when the ingredient lists are identical in content and order. */
  ingredients: IngredientDiff | null;
}

const FIELDS: ReadonlyArray<{ field: keyof DiffableForm; label: string }> = [
  { field: 'name', label: 'Name' },
  { field: 'recipeType', label: 'Type' },
  { field: 'description', label: 'Description' },
  { field: 'instructions', label: 'Instructions' },
  { field: 'sourceUrl', label: 'Source URL' },
  { field: 'servings', label: 'Servings' },
  { field: 'prepTimeMinutes', label: 'Prep time (min)' },
  { field: 'cookTimeMinutes', label: 'Cook time (min)' },
  { field: 'imagePath', label: 'Image' },
];

/** Blank → null, else trimmed; numbers pass through. Mirrors the save body's nullIfBlank. */
function norm(value: string | number | null | undefined): string | number | null {
  if (value == null) return null;
  if (typeof value === 'number') return value;
  const t = value.trim();
  return t === '' ? null : t;
}

function show(value: string | number | null): string {
  return value == null ? '' : String(value);
}

/** One ingredient as a display line: `qty unit name, notes [group] · category`, normalized. */
export function ingredientLine(i: DiffableIngredient): string {
  const parts = [norm(i.quantity), norm(i.unit), norm(i.name)].filter((p) => p != null).map(String);
  let line = parts.join(' ');
  const notes = norm(i.notes);
  if (notes != null) line += `, ${notes}`;
  const group = norm(i.groupName);
  if (group != null) line += ` [${group}]`;
  const category = norm(i.category);
  if (category != null) line += ` · ${category}`;
  return line;
}

/** The comparison key: every normalized field, structured, so values that merely display alike (1 cup + rice vs
 *  1 + "cup rice") never compare equal. The display line is derived separately. */
function ingredientKey(i: DiffableIngredient): string {
  return JSON.stringify([norm(i.quantity), norm(i.unit), norm(i.name), norm(i.category), norm(i.notes), norm(i.groupName)]);
}

/** Multiset difference a − b by key, keeping a's items. */
function minus(a: ReadonlyArray<DiffableIngredient>, b: ReadonlyArray<DiffableIngredient>): DiffableIngredient[] {
  const counts = new Map<string, number>();
  for (const item of b) {
    const key = ingredientKey(item);
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  const out: DiffableIngredient[] = [];
  for (const item of a) {
    const key = ingredientKey(item);
    const n = counts.get(key) ?? 0;
    if (n > 0) counts.set(key, n - 1);
    else out.push(item);
  }
  return out;
}

export function diffIngredients(
  yours: ReadonlyArray<DiffableIngredient>,
  theirs: ReadonlyArray<DiffableIngredient>,
): IngredientDiff | null {
  const mine = yours.map(ingredientKey);
  const latest = theirs.map(ingredientKey);
  if (mine.length === latest.length && mine.every((key, i) => key === latest[i])) return null;
  const onlyYours = minus(yours, theirs).map(ingredientLine);
  const onlyTheirs = minus(theirs, yours).map(ingredientLine);
  return { onlyYours, onlyTheirs, reorderedOnly: onlyYours.length === 0 && onlyTheirs.length === 0 };
}

/**
 * The field-level diff of the user's form against the latest saved recipe. `theirsIngredients` is taken in
 * its saved order (sortOrder ascending), the same order the editor shows after a reload.
 */
export function diffRecipe(
  form: DiffableForm,
  ingredients: ReadonlyArray<DiffableIngredient>,
  latest: RecipeFullDto,
): RecipeConflictDiff {
  const theirsForm: DiffableForm = {
    name: latest.name,
    description: latest.description ?? '',
    instructions: latest.instructions ?? '',
    sourceUrl: latest.sourceUrl ?? '',
    prepTimeMinutes: latest.prepTimeMinutes,
    cookTimeMinutes: latest.cookTimeMinutes,
    servings: latest.servings,
    recipeType: latest.recipeType,
    imagePath: latest.imagePath,
  };
  const fields: FieldDiff[] = [];
  for (const { field, label } of FIELDS) {
    const yours = norm(form[field]);
    const theirs = norm(theirsForm[field]);
    if (yours !== theirs) fields.push({ field, label, yours: show(yours), theirs: show(theirs) });
  }
  const theirsIngredients = latest.ingredients.slice().sort((a, b) => a.sortOrder - b.sortOrder);
  return { fields, ingredients: diffIngredients(ingredients, theirsIngredients) };
}

/** True when nothing differs: keeping yours would change no content. */
export function isEmptyDiff(diff: RecipeConflictDiff): boolean {
  return diff.fields.length === 0 && diff.ingredients == null;
}
