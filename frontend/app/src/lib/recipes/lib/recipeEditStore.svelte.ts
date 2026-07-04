// ─────────────────────────────────────────────────────────────────────────
// Edit-view store — the source of truth for the recipe edit form
// (#recipes-edit-root). Owns ONLY edit-view files (spec §6 store split) so
// WP-5 / WP-6 never collide.
//
// ⚠ Svelte 5 rune rule: export the class INSTANCE, not reassigned $state.
//
// Mirrors RecipeEdit.razor: draft-first load (restore + toast, else recipe-or-
// blank), 2s-debounce autosave drafts, ingredient add/bulk/remove(undo)/reorder,
// image upload/remove/pick, and Save/Cancel/Delete. Versionless / last-write-wins
// (D5): on any 4xx during save/delete we surface a calm "this recipe changed"
// and return to the list (§8). Single-user form ⇒ NO liveness (autosave is the
// persistence path).
// ─────────────────────────────────────────────────────────────────────────

import type {
  RecipeDraftData,
  RecipeFullDto,
  RecipeType,
  RecipeWriteRequest,
  SaveDraftRequest,
  ShellContext,
} from './types';
import {
  ApiError,
  getRecipe,
  getDraft,
  saveDraft,
  deleteDraft,
  createRecipe,
  updateRecipe,
  deleteRecipe,
  uploadImage,
  getCategories,
} from './api';
import { base } from '$app/paths';
import { showToast } from '$lib/shared/toast-store.svelte';

/** The bound form model (text fields are strings; trimmed/null-on-save). */
export interface RecipeEditModel {
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

/** One ingredient row. `id` is a stable client uid for keying + svelte-dnd-action. */
export interface IngredientRow {
  id: number;
  ingredientId: number;
  quantity: number | null;
  unit: string | null;
  name: string;
  category: string;
  notes: string | null;
  groupName: string | null;
  sortOrder: number;
}

/** A structured ingredient produced by the entry / bulk-paste (pre-row). */
export interface NewIngredientInput {
  name: string;
  quantity: number | null;
  unit: string | null;
  category: string;
  notes: string | null;
  groupName?: string | null;
}

type AutosaveStatus = 'none' | 'saving' | 'saved';

const AUTOSAVE_DELAY_MS = 2000;
const SAVED_CLEAR_MS = 3000;

function blankForm(): RecipeEditModel {
  return {
    name: '',
    description: '',
    instructions: '',
    sourceUrl: '',
    prepTimeMinutes: null,
    cookTimeMinutes: null,
    servings: null,
    recipeType: 'main',
    imagePath: null,
  };
}

class RecipeEditStore {
  form = $state<RecipeEditModel>(blankForm());
  ingredients = $state<IngredientRow[]>([]);
  categories = $state<string[]>([]);
  loading = $state(true);
  saving = $state(false);
  deleting = $state(false);
  dirty = $state(false);
  autosaveStatus = $state<AutosaveStatus>('none');
  error = $state<string | null>(null);

  /** The recipe id (null = new). Set from the shell context. */
  recipeId = $state<number | null>(null);
  currentUserId = $state(0);

  isEdit = $derived(this.recipeId != null);

  private uidSeq = 1;
  private autosaveTimer: ReturnType<typeof setTimeout> | null = null;
  private savedClearTimer: ReturnType<typeof setTimeout> | null = null;
  /** The in-flight draft-save (if any) — save()/delete() await it before deleteDraft so a late
   *  autosave can't re-create the draft we just cleared (council race fix). */
  private autosaveInFlight: Promise<void> | null = null;

  init(ctx: ShellContext): void {
    this.currentUserId = ctx.userId;
    this.recipeId = ctx.recipeId;
  }

  /**
   * Load draft-first (restore + toast), else the existing recipe (edit) or blank
   * defaults (new). Mirrors RecipeEdit.razor LoadRecipeOrDraft. Also loads the
   * household categories for the entry select.
   */
  async load(recipeId: number | null): Promise<void> {
    this.recipeId = recipeId;
    this.loading = true;
    this.error = null;
    try {
      const draft = await getDraft(recipeId);
      if (draft) {
        this.applyDraft(draft);
        showToast({ message: 'Restored from draft', kind: 'info' });
      } else if (recipeId != null) {
        const full = await getRecipe(recipeId);
        this.applyFull(full);
      } else {
        // New recipe defaults (RecipeEdit.razor:352-357).
        this.form = { ...blankForm(), recipeType: 'main', servings: 4 };
        this.ingredients = [];
      }
      this.dirty = false;
    } catch (e) {
      if (recipeId != null && e instanceof ApiError) {
        // Missing / cross-household recipe → calm message + back to the list.
        showToast({ message: 'Recipe not found.', kind: 'error' });
        this.navigateToList();
        return;
      }
      this.error = e instanceof Error ? e.message : String(e);
    } finally {
      this.loading = false;
    }
    // Categories are non-critical — load after the form is up.
    void this.loadCategories();
  }

  private async loadCategories(): Promise<void> {
    try {
      const cats = await getCategories();
      this.categories = cats.map((c) => c.name);
    } catch {
      this.categories = [];
    }
  }

  private applyDraft(d: RecipeDraftData): void {
    this.form = {
      name: d.name,
      description: d.description ?? '',
      instructions: d.instructions ?? '',
      sourceUrl: d.sourceUrl ?? '',
      prepTimeMinutes: d.prepTimeMinutes,
      cookTimeMinutes: d.cookTimeMinutes,
      servings: d.servings,
      // Drafts don't persist recipeType (DraftService.RecipeDraftData has no type) —
      // mirrors Blazor restoring a draft into a default-Main recipe.
      recipeType: 'main',
      imagePath: d.imagePath,
    };
    this.ingredients = d.ingredients.map((i) => this.toRow(i, 0));
  }

  private applyFull(f: RecipeFullDto): void {
    this.form = {
      name: f.name,
      description: f.description ?? '',
      instructions: f.instructions ?? '',
      sourceUrl: f.sourceUrl ?? '',
      prepTimeMinutes: f.prepTimeMinutes,
      cookTimeMinutes: f.cookTimeMinutes,
      servings: f.servings,
      recipeType: f.recipeType,
      imagePath: f.imagePath,
    };
    this.ingredients = f.ingredients
      .slice()
      .sort((a, b) => a.sortOrder - b.sortOrder)
      .map((i) => this.toRow(i, i.ingredientId));
  }

  private toRow(
    i: {
      quantity: number | null;
      unit: string | null;
      name: string;
      category: string;
      notes: string | null;
      groupName: string | null;
      sortOrder: number;
    },
    ingredientId: number,
  ): IngredientRow {
    return {
      id: this.uidSeq++,
      ingredientId,
      quantity: i.quantity,
      unit: i.unit,
      name: i.name,
      category: i.category,
      notes: i.notes,
      groupName: i.groupName,
      sortOrder: i.sortOrder,
    };
  }

  // ── Field edits → dirty + autosave ─────────────────────────────────────────

  /** Call on any form-field edit: marks dirty + (re)schedules the draft autosave. */
  onEdit(): void {
    this.dirty = true;
    this.autosaveStatus = 'none';
    this.scheduleAutosave();
  }

  private scheduleAutosave(): void {
    if (this.autosaveTimer) clearTimeout(this.autosaveTimer);
    this.autosaveTimer = setTimeout(() => void this.runAutosave(), AUTOSAVE_DELAY_MS);
  }

  private async runAutosave(): Promise<void> {
    // Skip if: the draft endpoint requires a non-blank name, OR a save/delete is underway (that path
    // owns the draft lifecycle and will deleteDraft — autosaving now would resurrect it).
    if (!this.form.name.trim() || this.saving || this.deleting) {
      this.autosaveStatus = 'none';
      return;
    }
    this.autosaveStatus = 'saving';
    const inFlight = (async () => {
      try {
        await saveDraft(this.buildDraftBody());
        this.autosaveStatus = 'saved';
        if (this.savedClearTimer) clearTimeout(this.savedClearTimer);
        this.savedClearTimer = setTimeout(() => {
          if (this.autosaveStatus === 'saved') this.autosaveStatus = 'none';
        }, SAVED_CLEAR_MS);
      } catch {
        this.autosaveStatus = 'none';
        showToast({ message: "Couldn't save draft.", kind: 'info' });
      }
    })();
    this.autosaveInFlight = inFlight;
    await inFlight;
    if (this.autosaveInFlight === inFlight) this.autosaveInFlight = null;
  }

  /**
   * Cancel a pending autosave timer and wait out any in-flight draft save. save()/delete() call this
   * before deleteDraft so a late autosave PUT can't land after the delete and re-create the draft.
   */
  private async flushAutosave(): Promise<void> {
    if (this.autosaveTimer) {
      clearTimeout(this.autosaveTimer);
      this.autosaveTimer = null;
    }
    if (this.autosaveInFlight) {
      try {
        await this.autosaveInFlight;
      } catch {
        /* the autosave handles its own errors */
      }
    }
  }

  // ── Ingredients ────────────────────────────────────────────────────────────

  addIngredient(input: NewIngredientInput): void {
    this.ingredients = [...this.ingredients, this.newRow(input, this.ingredients.length)];
    this.onEdit();
  }

  addBulk(inputs: NewIngredientInput[]): void {
    let order = this.ingredients.length;
    const rows = inputs.map((i) => this.newRow(i, order++));
    this.ingredients = [...this.ingredients, ...rows];
    this.onEdit();
  }

  private newRow(input: NewIngredientInput, sortOrder: number): IngredientRow {
    return {
      id: this.uidSeq++,
      ingredientId: 0,
      quantity: input.quantity,
      unit: input.unit,
      name: input.name,
      category: input.category,
      notes: input.notes,
      groupName: input.groupName ?? null,
      sortOrder,
    };
  }

  /** Remove an ingredient with a 5s "Undo" toast (mirrors IngredientList delete-with-undo). */
  removeIngredient(id: number): void {
    const index = this.ingredients.findIndex((r) => r.id === id);
    if (index < 0) return;
    const removed = this.ingredients[index];
    this.ingredients = this.resequence(this.ingredients.filter((r) => r.id !== id));
    this.onEdit();
    showToast({
      message: 'Ingredient deleted',
      kind: 'info',
      durationMs: 5000,
      action: {
        label: 'Undo',
        onClick: () => this.restoreIngredient(removed, index),
      },
    });
  }

  private restoreIngredient(row: IngredientRow, index: number): void {
    const next = this.ingredients.slice();
    next.splice(Math.min(index, next.length), 0, row);
    this.ingredients = this.resequence(next);
    this.onEdit();
    showToast({ message: 'Ingredient restored', kind: 'success' });
  }

  /**
   * "Edit" an ingredient — parity with Blazor's remove-and-re-add flow
   * (IngredientList.razor:451-456). Inline editing is a harvested follow-up quest.
   */
  editIngredient(id: number): void {
    this.ingredients = this.resequence(this.ingredients.filter((r) => r.id !== id));
    this.onEdit();
    showToast({ message: 'Re-add the ingredient with your changes.', kind: 'info' });
  }

  /** Apply a drag-reorder (svelte-dnd-action finalize gives the new row order). */
  reorder(rows: IngredientRow[]): void {
    this.ingredients = this.resequence(rows);
    this.onEdit();
  }

  /** Recompute sortOrder 0..n in list order. */
  private resequence(rows: IngredientRow[]): IngredientRow[] {
    return rows.map((r, i) => ({ ...r, sortOrder: i }));
  }

  // ── Image ──────────────────────────────────────────────────────────────────

  async uploadImage(file: File): Promise<void> {
    try {
      const { imagePath } = await uploadImage(file);
      this.form.imagePath = imagePath;
      this.onEdit();
      showToast({ message: 'Image uploaded', kind: 'success' });
    } catch (e) {
      const msg =
        e instanceof ApiError && e.status === 400
          ? 'That image was rejected (max 10 MB; JPG/PNG/GIF/WebP).'
          : "Couldn't upload that image.";
      showToast({ message: msg, kind: 'error' });
    }
  }

  removeImage(): void {
    this.form.imagePath = null;
    this.onEdit();
  }

  setImage(path: string): void {
    this.form.imagePath = path;
    this.onEdit();
  }

  // ── Save / Delete / Cancel ───────────────────────────────────────────────────

  async save(): Promise<void> {
    const name = this.form.name.trim();
    if (!name) {
      showToast({ message: 'Recipe name is required.', kind: 'error' });
      return;
    }
    this.saving = true; // set first: a timer-fired autosave now bails on the saving guard
    await this.flushAutosave(); // wait out any in-flight draft save before we deleteDraft
    this.error = null;
    try {
      const body = this.buildWriteRequest();
      if (this.recipeId != null) {
        await updateRecipe(this.recipeId, body);
      } else {
        await createRecipe(body);
      }
      await this.safeDeleteDraft();
      this.dirty = false; // suppress the beforeunload nav-lock before navigating
      this.navigateToList();
    } catch (e) {
      if (e instanceof ApiError) {
        // 4xx incl. the empty-400-from-404 quirk → concurrent delete / last-write-wins.
        this.error = 'This recipe changed since you opened it.';
        this.dirty = false;
        showToast({ message: 'This recipe changed — returning to the list.', kind: 'info' });
        this.navigateToList();
      } else {
        showToast({ message: 'Something went wrong saving. Please try again.', kind: 'error' });
      }
    } finally {
      this.saving = false;
    }
  }

  async delete(): Promise<void> {
    if (this.recipeId == null) return;
    this.deleting = true; // set first: a timer-fired autosave now bails on the deleting guard
    await this.flushAutosave(); // wait out any in-flight draft save before we deleteDraft
    try {
      await deleteRecipe(this.recipeId);
      await this.safeDeleteDraft();
      this.dirty = false;
      showToast({ message: 'Recipe deleted', kind: 'success' });
      this.navigateToList();
    } catch (e) {
      if (e instanceof ApiError) {
        // Already gone — just return to the list.
        this.dirty = false;
        showToast({ message: 'That recipe was already removed.', kind: 'info' });
        this.navigateToList();
      } else {
        showToast({ message: "Couldn't delete that recipe right now.", kind: 'error' });
      }
    } finally {
      this.deleting = false;
    }
  }

  cancel(): void {
    this.dirty = false; // explicit discard — no nav-lock prompt
    this.navigateToList();
  }

  private async safeDeleteDraft(): Promise<void> {
    try {
      await deleteDraft(this.recipeId);
    } catch {
      /* draft cleanup is best-effort */
    }
  }

  private navigateToList(): void {
    window.location.assign(`${base}/recipes`);
  }

  /** Clear timers on unmount (EditApp's effect cleanup). */
  teardown(): void {
    if (this.autosaveTimer) clearTimeout(this.autosaveTimer);
    if (this.savedClearTimer) clearTimeout(this.savedClearTimer);
    this.autosaveTimer = null;
    this.savedClearTimer = null;
  }

  // ── Mappers (form → wire) ────────────────────────────────────────────────────

  private buildWriteRequest(): RecipeWriteRequest {
    return {
      name: this.form.name.trim(),
      description: nullIfBlank(this.form.description),
      instructions: nullIfBlank(this.form.instructions),
      sourceUrl: nullIfBlank(this.form.sourceUrl),
      servings: this.form.servings,
      prepTimeMinutes: this.form.prepTimeMinutes,
      cookTimeMinutes: this.form.cookTimeMinutes,
      recipeType: this.form.recipeType,
      imagePath: this.form.imagePath,
      ingredients: this.ingredients.map((r, i) => ({
        name: r.name.trim(),
        quantity: r.quantity,
        unit: nullIfBlank(r.unit),
        category: r.category?.trim() || this.fallbackCategory(),
        notes: nullIfBlank(r.notes),
        groupName: nullIfBlank(r.groupName),
        sortOrder: i,
      })),
    };
  }

  private buildDraftBody(): SaveDraftRequest {
    return {
      recipeId: this.recipeId,
      name: this.form.name.trim(),
      description: nullIfBlank(this.form.description),
      instructions: nullIfBlank(this.form.instructions),
      imagePath: this.form.imagePath,
      sourceUrl: nullIfBlank(this.form.sourceUrl),
      servings: this.form.servings,
      prepTimeMinutes: this.form.prepTimeMinutes,
      cookTimeMinutes: this.form.cookTimeMinutes,
      ingredients: this.ingredients.map((r, i) => ({
        name: r.name.trim(),
        quantity: r.quantity,
        unit: nullIfBlank(r.unit),
        category: r.category?.trim() || this.fallbackCategory(),
        notes: nullIfBlank(r.notes),
        groupName: nullIfBlank(r.groupName),
        sortOrder: i,
      })),
    };
  }

  private fallbackCategory(): string {
    return this.categories[0] ?? 'Pantry';
  }
}

function nullIfBlank(s: string | null | undefined): string | null {
  return s == null || s.trim() === '' ? null : s.trim();
}

/** The single shared edit-view store instance (export the instance, not the runes). */
export const recipeEditStore = new RecipeEditStore();
