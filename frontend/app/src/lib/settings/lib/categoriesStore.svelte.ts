// ─────────────────────────────────────────────────────────────────────────
// Categories view store (#settings-categories-root). Source of truth for the
// active + deleted category lists.
//
// ⚠ Svelte 5 rune rule (global CORRECTION): never `export` a reassigned
// `$state`. We wrap the mutable state in a class instance and export the instance.
//
// Parity-first ⇒ versionless / last-write-wins. This view is WRITE-heavy but
// each surface is simple CRUD. Load and write ordering live in a Resource
// (quest 41337b6f): every write runs inside its window (no load commits over a
// pending write), and the reload a write asks for runs once when the last write
// settles. No autosave/draft — these are explicit button actions. No liveness
// (parity: the page doesn't poll).
// ─────────────────────────────────────────────────────────────────────────

import type { CategoryDto, CategoryListDto, CategoryWriteRequest } from './types';
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
import { Resource } from '$lib/data/resource.svelte';
import { showToast } from '$lib/shared/toast-store.svelte';

class CategoriesStore {
  active = $state<CategoryDto[]>([]);
  deleted = $state<CategoryDto[]>([]);
  loading = $state(true);
  error = $state<string | null>(null);

  /** True once the first load resolved — gates the skeleton so reloads don't reflash it. */
  private hasLoaded = false;
  /** Bumped by every optimistic reorder: an undo applies only while its own order is the latest. */
  #reorderGen = 0;

  #resource = new Resource<CategoryListDto>({
    fetch: () => getCategories(),
    commit: (dto) => {
      this.active = dto.active;
      this.deleted = dto.deleted;
      this.hasLoaded = true;
    },
    onLoadStart: () => {
      if (!this.hasLoaded) this.loading = true; // spinner only on the FIRST load
      this.error = null;
    },
    onLoadError: (e) => {
      this.error = describe(e, 'load categories');
    },
    onLoadIdle: () => {
      this.loading = false;
    },
  });

  /** Load both lists. Spinner only on the FIRST load; reloads refresh in place. */
  async load(): Promise<void> {
    await this.#resource.load();
  }

  /**
   * A write-then-reload action: `run()` with the default reload 'always', so the lists refresh once
   * the write settles, success or failure. The success toast sits outside the write window.
   */
  async #write<R>(call: () => Promise<R>, action: string, success: (result: R) => string): Promise<boolean> {
    let result: R;
    try {
      result = await this.#resource.run({ call });
    } catch (e) {
      showToast({ message: describe(e, action), kind: 'error' });
      return false;
    }
    showToast({ message: success(result), kind: 'success' });
    return true;
  }

  async add(body: CategoryWriteRequest): Promise<boolean> {
    return this.#write(() => createCategory(body), 'create category', (created) => `Category "${created.name}" created.`);
  }

  async update(categoryId: number, body: CategoryWriteRequest): Promise<boolean> {
    return this.#write(() => updateCategory(categoryId, body), 'update category', () => 'Category updated.');
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
    await this.#write(() => deleteCategory(categoryId), 'delete category', () => 'Category deleted.');
  }

  async restore(categoryId: number): Promise<void> {
    await this.#write(() => restoreCategory(categoryId), 'restore category', () => 'Category restored.');
  }

  /**
   * Persist a new order (parity: reorder commits immediately). Optimistic local reorder, then persist.
   * On a 409/other 4xx the old order comes back and the lists reload; on a 5xx/network failure the old
   * order comes back with no reload (spec §5 row 6). The undo is scoped: it applies only while this
   * reorder's order is still the latest, so it never erases a later drag. When a later drag overlapped
   * it, no local snapshot is known to be the saved order (both may fail — slim-review on fca#152), so
   * the failure reloads the server order instead. That load is requested from INSIDE the write window
   * (the undo runs before settle), so the Resource defers it and coalesces it with any reconcile the
   * same settle starts — one GET, never two racing ones (slim-review delta on fca#152).
   */
  async reorder(orderedIds: number[]): Promise<void> {
    await this.#resource.mutate({
      optimistic: () => {
        const byId = new Map(this.active.map((c) => [c.categoryId, c]));
        const next = orderedIds.map((id) => byId.get(id)).filter((c): c is CategoryDto => c != null);
        if (next.length !== this.active.length) return;
        const previous = this.active;
        const gen = ++this.#reorderGen;
        this.active = next;
        return () => {
          if (this.#reorderGen === gen) this.active = previous;
          else void this.#resource.load(); // 'deferred' (this write is still pending): owed at settle
        };
      },
      call: () => updateSortOrder(orderedIds),
      messages: {
        conflict: 'The categories changed — refreshing the order.',
        rejected: "Couldn't save that order — refreshing the list.",
        failed: "Couldn't save that order right now.",
      },
    });
  }
}

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

/** The single shared categories-view store instance (export the instance, not the runes). */
export const categoriesStore = new CategoriesStore();
