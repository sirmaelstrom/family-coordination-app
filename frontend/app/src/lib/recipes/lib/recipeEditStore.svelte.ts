// ─────────────────────────────────────────────────────────────────────────
// Edit-view store — the source of truth for the recipe edit form
// (#recipes-edit-root). Owns ONLY edit-view files (spec §6 store split) so
// WP-5 / WP-6 never collide.
//
// ⚠ Svelte 5 rune rule: export the class INSTANCE, not reassigned $state.
//
// Mirrors RecipeEdit.razor: draft-first load (restore + toast, else recipe-or-
// blank), 2s-debounce autosave drafts, ingredient add/bulk/remove(undo)/inline-
// edit/reorder, image upload/remove/pick, and Save/Cancel/Delete. Single-user
// form ⇒ NO liveness (autosave is the persistence path).
//
// Concurrency: the full-form save echoes the xmin token (`version` from GET);
// a 409 means someone else saved since we loaded → non-destructive conflict
// banner + an explicit Reload action (reloadAfterConflict). Other 4xx during
// save/delete keep the old calm "this recipe changed" → back-to-list path.
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

/** The inline-editable fields of a row (mirrors what the add-entry collects). */
export interface IngredientUpdate {
  name: string;
  quantity: number | null;
  unit: string | null;
  category: string;
  notes: string | null;
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
  /** A save hit a 409 (someone else changed the recipe) — show the reload banner, keep the form. */
  conflict = $state(false);
  /** Reload-after-conflict in progress (disables the banner button). */
  reloading = $state(false);
  /** A draft is known to exist for this editor (restored on load, autosaved, or kept by a conflict reload).
   *  Shows the Discard-draft control (quest adfe3d57). */
  hasDraft = $state(false);
  /** Discard-draft in progress (disables the control; a timer-fired autosave bails on it). */
  discarding = $state(false);

  /** The recipe id (null = new). Set from the shell context. */
  recipeId = $state<number | null>(null);
  currentUserId = $state(0);

  isEdit = $derived(this.recipeId != null);

  /** The xmin token the form's content is based on — from the last GET, or a restored draft's base version
   *  (quest 0c97a292). Echoed on the update PUT and stored as the draft's baseVersion (null until loaded). */
  private serverVersion: number | null = null;
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
    this.conflict = false;
    this.hasDraft = false;
    this.serverVersion = null;
    try {
      const draft = await getDraft(recipeId);
      if (draft) {
        this.applyDraft(draft);
        this.hasDraft = true;
        showToast({ message: 'Restored from draft', kind: 'info' });
        if (recipeId != null && draft.baseVersion != null) {
          // The draft carries the token its edits started from (quest 0c97a292). Save with THAT, not the live
          // one: if someone saved the recipe after the draft began, the PUT answers 409 and the reload banner
          // shows, instead of the draft silently overwriting their edit. No GET here, so a draft for a recipe
          // deleted meanwhile opens and learns of the delete at save (404 → back to the list).
          this.serverVersion = draft.baseVersion;
        } else if (recipeId != null) {
          // A draft stored before drafts carried a base version (or saved while the editor held no token).
          // Best-effort fetch the current token so the save is still guarded. On failure keep null: the server
          // answers a tokenless PUT with 409 (quest 8cf3c806), so the save shows the reload banner instead of
          // overwriting (a deleted recipe answers 404). Residual for these legacy drafts only: the live token
          // can't see an edit made between the draft's start and now, and the next autosave stores that live
          // token as the draft's base, so the unknown base is adopted rather than kept unknown.
          try {
            this.serverVersion = (await getRecipe(recipeId)).version;
          } catch {
            this.serverVersion = null;
          }
        }
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
    this.serverVersion = f.version;
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
    // Skip if: the draft endpoint requires a non-blank name, OR a save/delete/discard is underway (that path
    // owns the draft lifecycle and will deleteDraft — autosaving now would resurrect it).
    if (!this.form.name.trim() || this.saving || this.deleting || this.discarding) {
      this.autosaveStatus = 'none';
      return;
    }
    this.autosaveStatus = 'saving';
    const inFlight = (async () => {
      try {
        await saveDraft(this.buildDraftBody());
        this.hasDraft = true;
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
   * Inline-edit an ingredient in place (replaces the old Blazor remove-and-re-add flow). Only the
   * entry-form fields are editable; groupName / sortOrder / ingredientId are preserved. Persistence is
   * unchanged: ingredients are client state, saved wholesale by the recipe PUT (and the draft autosave).
   */
  updateIngredient(id: number, patch: IngredientUpdate): void {
    this.ingredients = this.ingredients.map((r) => (r.id === id ? { ...r, ...patch } : r));
    this.onEdit();
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
    this.conflict = false;
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
      if (e instanceof ApiError && e.status === 409) {
        // Stale xmin token — someone else saved since we loaded. NON-destructive: keep the form (and
        // dirty, so the nav-lock still guards) and show the reload banner instead of navigating away.
        this.conflict = true;
      } else if (e instanceof ApiError) {
        // Other 4xx (e.g. a 404 from a concurrent delete). On a 404 the recipe is gone, so its draft can never
        // save: delete it, or the draft-first load reopens it on every visit (quest adfe3d57). Other 4xx keep it.
        if (e.status === 404) await this.safeDeleteDraft();
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

  /**
   * Explicit conflict recovery: refetch the latest recipe and replace the form with it. The user's typed
   * state is first persisted as the draft (best-effort) so it isn't silently discarded — reopening the
   * editor restores it via the normal draft-first load. Deliberately NOT the draft-first load() (that
   * would restore the very edits that just conflicted instead of showing the latest server state).
   */
  async reloadAfterConflict(): Promise<void> {
    if (this.recipeId == null || this.reloading) return;
    this.reloading = true;
    try {
      if (this.form.name.trim()) {
        try {
          await saveDraft(this.buildDraftBody()); // preserve what the user typed (draft needs a name)
          this.hasDraft = true;
        } catch {
          /* best-effort — reload anyway */
        }
      }
      const full = await getRecipe(this.recipeId);
      this.applyFull(full); // also refreshes serverVersion for the next save
      this.conflict = false;
      this.error = null;
      this.dirty = false;
      // The draft saved above keeps the conflicted base (serverVersion was still the old token), so reopening the
      // editor shows those edits again and their save answers 409 again: they were made on the old version.
      // Editing this reloaded form autosaves over that draft with the new base; saving it deletes the draft.
      showToast({
        message: 'Loaded the latest version. Your edits are kept as a draft until you edit or save this version.',
        kind: 'info',
      });
    } catch (e) {
      if (e instanceof ApiError) {
        // The recipe was deleted meanwhile — nothing to reload into. On a 404 the draft just saved above is an
        // orphan that could never save, so delete it (same reason as save()'s 404 branch).
        if (e.status === 404) {
          await this.flushAutosave(); // a late autosave PUT must not re-create it after the delete
          await this.safeDeleteDraft();
        }
        this.dirty = false;
        showToast({ message: 'That recipe was removed — returning to the list.', kind: 'info' });
        this.navigateToList();
      } else {
        showToast({ message: "Couldn't reload the recipe. Please try again.", kind: 'error' });
      }
    } finally {
      this.reloading = false;
    }
  }

  /**
   * Explicit discard (quest adfe3d57): delete this editor's draft and show the saved recipe again (or a blank
   * form for a new one). It is the way out of a draft whose save keeps answering 409 when the user wants the
   * latest version, not their edits. If the delete fails the form and draft stay as they were.
   */
  async discardDraft(): Promise<void> {
    if (this.discarding || this.saving || this.deleting) return;
    this.discarding = true; // set first: a timer-fired autosave now bails on the discarding guard
    await this.flushAutosave(); // wait out any in-flight draft save so it can't re-create the draft after the delete
    try {
      try {
        await deleteDraft(this.recipeId);
      } catch {
        showToast({ message: "Couldn't discard the draft. Please try again.", kind: 'error' });
        return;
      }
      this.hasDraft = false;
      this.autosaveStatus = 'none';
      this.conflict = false;
      this.error = null;
      if (this.recipeId != null) {
        try {
          this.applyFull(await getRecipe(this.recipeId)); // also refreshes serverVersion for the next save
        } catch (e) {
          if (e instanceof ApiError) {
            this.dirty = false;
            showToast({ message: 'That recipe was removed — returning to the list.', kind: 'info' });
            this.navigateToList();
          } else {
            this.error = "Draft discarded, but the recipe couldn't be loaded. Please reload the page.";
          }
          return;
        }
      } else {
        this.form = { ...blankForm(), recipeType: 'main', servings: 4 };
        this.ingredients = [];
      }
      this.dirty = false;
      showToast({ message: 'Draft discarded.', kind: 'info' });
    } finally {
      this.discarding = false;
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
      // The xmin token echoed for the update PUT (null on create / when no fresh GET succeeded).
      version: this.serverVersion,
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
      // The token the edits started from, so a restored draft saves against its own base (quest 0c97a292).
      // After a conflict this is still the conflicted base until reload, so a draft saved then keeps it.
      baseVersion: this.recipeId != null ? this.serverVersion : null,
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
