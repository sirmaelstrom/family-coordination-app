// ─────────────────────────────────────────────────────────────────────────
// Categories view store (#settings-categories-root). Source of truth for the
// active + deleted category lists.
//
// ⚠ Svelte 5 rune rule (global CORRECTION): never `export` a reassigned
// `$state`. We wrap the mutable state in a class instance and export the instance.
//
// Parity-first ⇒ versionless / last-write-wins. This view is WRITE-heavy but
// each surface is simple CRUD: every mutation `await`s then reloads the affected
// lists; a `loadSeq` guard drops a stale reload that resolves after a newer one
// (memory fca-island-async-race-guards). No autosave/draft — these are explicit
// button actions. No liveness (parity: the page doesn't poll).
// ─────────────────────────────────────────────────────────────────────────

import type { CategoryDto, CategoryWriteRequest } from './types';
import {
  ApiError,
  getCategories,
  createCategory,
  updateCategory,
  deleteCategory,
  restoreCategory,
  updateSortOrder,
  categoryInUse,
} from './api';
import { showToast } from '$lib/shared/toast-store.svelte';

class CategoriesStore {
  active = $state<CategoryDto[]>([]);
  deleted = $state<CategoryDto[]>([]);
  loading = $state(true);
  error = $state<string | null>(null);

  /** True once the first load resolved — gates the skeleton so reloads don't reflash it. */
  private hasLoaded = false;
  /** Monotonic load id — a slower older response must not overwrite a newer one. */
  private seq = 0;

  /** Load both lists. Spinner only on the FIRST load; reloads refresh in place. */
  async load(): Promise<void> {
    const s = ++this.seq;
    try {
      if (!this.hasLoaded) this.loading = true;
      this.error = null;
      const dto = await getCategories();
      if (s !== this.seq) return; // a newer load superseded this one
      this.active = dto.active;
      this.deleted = dto.deleted;
      this.hasLoaded = true;
    } catch (e) {
      if (s !== this.seq) return;
      this.error = describe(e, 'load categories');
    } finally {
      if (s === this.seq) this.loading = false;
    }
  }

  async add(body: CategoryWriteRequest): Promise<boolean> {
    try {
      const created = await createCategory(body);
      await this.load();
      showToast({ message: `Category "${created.name}" created.`, kind: 'success' });
      return true;
    } catch (e) {
      showToast({ message: describe(e, 'create category'), kind: 'error' });
      return false;
    }
  }

  async update(categoryId: number, body: CategoryWriteRequest): Promise<boolean> {
    try {
      await updateCategory(categoryId, body);
      await this.load();
      showToast({ message: 'Category updated.', kind: 'success' });
      return true;
    } catch (e) {
      if (e instanceof ApiError) await this.load(); // reconcile to truth on 4xx
      showToast({ message: describe(e, 'update category'), kind: 'error' });
      return false;
    }
  }

  /** Whether the category's name is used by an ingredient (drives the delete confirm copy). */
  async checkInUse(categoryId: number): Promise<boolean> {
    try {
      return await categoryInUse(categoryId);
    } catch {
      return false; // non-critical — fall back to the plain confirm
    }
  }

  async remove(categoryId: number): Promise<void> {
    try {
      await deleteCategory(categoryId);
      await this.load();
      showToast({ message: 'Category deleted.', kind: 'success' });
    } catch (e) {
      if (e instanceof ApiError) await this.load();
      showToast({ message: describe(e, 'delete category'), kind: 'error' });
    }
  }

  async restore(categoryId: number): Promise<void> {
    try {
      await restoreCategory(categoryId);
      await this.load();
      showToast({ message: 'Category restored.', kind: 'success' });
    } catch (e) {
      if (e instanceof ApiError) await this.load();
      showToast({ message: describe(e, 'restore category'), kind: 'error' });
    }
  }

  /**
   * Persist a new order (parity: reorder commits immediately). Optimistic local
   * reorder so the UI is responsive, then persist; on failure reload to restore
   * the authoritative order + a toast.
   */
  async reorder(orderedIds: number[]): Promise<void> {
    const byId = new Map(this.active.map((c) => [c.categoryId, c]));
    const next = orderedIds.map((id) => byId.get(id)).filter((c): c is CategoryDto => c != null);
    if (next.length === this.active.length) this.active = next;
    try {
      await updateSortOrder(orderedIds);
    } catch (e) {
      await this.load(); // restore the real order
      showToast({ message: describe(e, 'reorder categories'), kind: 'error' });
    }
  }
}

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

/** The single shared categories-view store instance (export the instance, not the runes). */
export const categoriesStore = new CategoriesStore();
