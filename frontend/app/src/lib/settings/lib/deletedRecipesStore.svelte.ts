// ─────────────────────────────────────────────────────────────────────────
// Deleted Recipes view store (/settings/deleted-recipes). Source of truth for
// the household's deleted recipes and the Restore action.
//
// ⚠ Svelte 5 rune rule (global CORRECTION): never `export` a reassigned
// `$state`. We wrap the mutable state in a class instance and export the instance.
//
// Load and write ordering live in a Resource (quest 41337b6f). Restore goes
// through mutate(): the row leaves the list at once, and a failure puts that ONE
// row back at its index (an item-scoped undo, as recipeListStore's delete does).
// A 409/other 4xx also refetches the truth. The key stops a double Restore.
// No liveness: the page doesn't poll (parity with Categories).
// ─────────────────────────────────────────────────────────────────────────

import type { DeletedRecipeDto, DeletedRecipeListDto } from './types';
import { ApiError, getDeletedRecipes, restoreRecipe } from './api';
import { Resource } from '#lib/data/resource.svelte.ts';
import { showToast } from '#lib/shared/toast-store.svelte.ts';

class DeletedRecipesStore {
  /** Most recently deleted first (server order). */
  recipes = $state<DeletedRecipeDto[]>([]);
  loading = $state(true);
  error = $state<string | null>(null);

  /** True once the first load resolved — gates the skeleton so reloads don't reflash it. */
  private hasLoaded = false;

  #resource = new Resource<DeletedRecipeListDto>({
    fetch: () => getDeletedRecipes(),
    commit: (dto) => {
      this.recipes = dto.recipes;
      this.hasLoaded = true;
    },
    onLoadStart: () => {
      if (!this.hasLoaded) this.loading = true; // spinner only on the FIRST load
      this.error = null;
    },
    onLoadError: (e) => {
      this.error =
        e instanceof ApiError
          ? e.message || `Couldn't load deleted recipes (HTTP ${e.status}).`
          : e instanceof Error
            ? e.message
            : "Couldn't load deleted recipes.";
    },
    onLoadIdle: () => {
      this.loading = false;
    },
  });

  async load(): Promise<void> {
    await this.#resource.load();
  }

  /** Restore one recipe. The success toast sits outside the write window, so it can never take a failure path. */
  async restore(recipe: DeletedRecipeDto): Promise<void> {
    const id = recipe.recipeId;
    const outcome = await this.#resource.mutate({
      key: `restore:${id}`,
      optimistic: () => {
        const index = this.recipes.findIndex((r) => r.recipeId === id);
        if (index < 0) return;
        const removed = this.recipes[index];
        this.recipes = this.recipes.filter((r) => r.recipeId !== id);
        return () => {
          if (this.recipes.some((r) => r.recipeId === id)) return;
          const back = [...this.recipes];
          back.splice(Math.min(index, back.length), 0, removed);
          this.recipes = back;
        };
      },
      call: () => restoreRecipe(id),
      messages: {
        conflict: 'That recipe changed — refreshing the list. Try again.',
        rejected: "Couldn't restore that recipe — refreshing the list.",
        failed: "Couldn't restore that recipe right now.",
      },
    });
    if (outcome === 'ok') showToast({ message: `"${recipe.name}" restored.`, kind: 'success' });
  }
}

/** The single shared deleted-recipes store instance (export the instance, not the runes). */
export const deletedRecipesStore = new DeletedRecipesStore();
