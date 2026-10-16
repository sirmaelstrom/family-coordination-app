// ─────────────────────────────────────────────────────────────────────────
// List-view store — the source of truth for the recipes grid (#recipes-list-root).
//
// ⚠ Svelte 5 rune rule (global CORRECTION): never `export` a reassigned
// `$state`/`$derived` from a module. We wrap the mutable state in a class
// instance and export the instance — the runes live as fields on the object.
//
// Parity-first ⇒ versionless / last-write-wins. Search is SERVER-side + debounced
// (D9); the favorites filter is CLIENT-side over the loaded set; favoriteRecipeIds
// arrive with the same payload (one round-trip). Liveness re-runs the current
// query while the tab is visible.
//
// Ordering lives in a Resource (quest 41337b6f): no load is dispatched while a
// favorite/delete write is pending (it is owed and runs once at settle), and a
// load already in flight when a write starts is retired. Writes go through
// mutate(): a 409/other 4xx undoes + reconciles + a calm toast; a 5xx/network
// failure undoes with an error toast and no reconcile.
//
// Owns ONLY list-view files (spec §6 store split) — the edit view has its own
// store (recipeEditStore.svelte.ts), so WP-5 / WP-6 never collide.
// ─────────────────────────────────────────────────────────────────────────

import type {
  ConnectedHouseholdDto,
  RecipeListItemDto,
  ShellContext,
} from './types';
import type { RecipeListDto } from './types';
import { Resource } from '$lib/data/resource.svelte';
import {
  ApiError,
  listRecipes,
  listConnectedRecipes,
  toggleFavorite as apiToggleFavorite,
  deleteRecipe as apiDeleteRecipe,
  getConnections,
} from './api';
import { showToast } from '$lib/shared/toast-store.svelte';

class RecipeListStore {
  /** The active search term (server-filtered, debounced by the view). */
  query = $state('');
  /** The loaded card set (own household OR a connected household). */
  recipes = $state<RecipeListItemDto[]>([]);
  /** This user's favorite recipe ids (own household only — empty for connected). */
  favoriteIds = $state<Set<number>>(new Set());
  /** Client-side "favorites only" filter chip (own household only). */
  showFavoritesOnly = $state(false);
  /** null = my recipes; a household id = viewing that connected household (read-only). */
  selectedConnectedId = $state<number | null>(null);
  /** Connected households for the selector (empty ⇒ no selector shown). */
  connections = $state<ConnectedHouseholdDto[]>([]);
  loading = $state(true);
  error = $state<string | null>(null);
  currentUserId = $state(0);

  /** True once the first load has resolved — gates the skeleton so search/liveness don't reflash it. */
  private hasLoaded = false;
  /** Load + write ordering. Search / connected-switch / liveness / reconcile all call load(); the
   *  Resource applies only the latest load, and none that overlaps a pending favorite/delete write. */
  #resource = new Resource<{ connected: number | null; dto: RecipeListDto }>({
    fetch: async () => {
      const connected = this.selectedConnectedId;
      const dto =
        connected == null
          ? await listRecipes(this.query)
          : await listConnectedRecipes(connected, this.query);
      return { connected, dto };
    },
    commit: ({ connected, dto }) => {
      this.recipes = dto.recipes;
      this.favoriteIds = connected == null ? new Set(dto.favoriteRecipeIds) : new Set();
      this.hasLoaded = true;
    },
    onLoadStart: () => {
      if (!this.hasLoaded) this.loading = true; // spinner only on the FIRST load
      this.error = null;
    },
    onLoadError: (e) => {
      this.error =
        e instanceof ApiError
          ? `Failed to load recipes (HTTP ${e.status}).`
          : e instanceof Error
            ? e.message
            : String(e);
    },
    onLoadIdle: () => {
      this.loading = false;
    },
  });
  /** The list refetch hook (ListApp's loader), shared by liveness + error reconcile. */
  private refresh: (() => Promise<void>) | null = null;

  /** Viewing a connected household? (favorites + per-card heart hide when true.) */
  isViewingConnected = $derived(this.selectedConnectedId != null);

  /** The connected household's name (attribution) — null when viewing my recipes. */
  selectedConnectedName = $derived(
    this.selectedConnectedId == null
      ? null
      : (this.connections.find((c) => c.householdId === this.selectedConnectedId)?.householdName ?? null),
  );

  /** The cards actually rendered — applies the client-side favorites filter (own household only). */
  displayed = $derived.by(() =>
    this.showFavoritesOnly && this.selectedConnectedId == null
      ? this.recipes.filter((r) => this.favoriteIds.has(r.recipeId))
      : this.recipes,
  );

  init(ctx: ShellContext): void {
    this.currentUserId = ctx.userId;
  }

  setRefresh(refresh: () => Promise<void>): void {
    this.refresh = refresh;
  }

  /** Re-run the current query. Shared by liveness + error reconcile. */
  async reconcile(): Promise<void> {
    if (this.refresh) await this.refresh();
  }

  /**
   * Load the current view: own recipes (#1) when no connected household is
   * selected, else the connected household's shared recipes (#15, same shape,
   * favorites always empty). Spinner only on the FIRST load — search + liveness
   * refresh in place (no skeleton reflash).
   */
  async load(): Promise<void> {
    await this.#resource.load();
  }

  /** Load the connected-household selector chips (non-critical: failure ⇒ no selector). */
  async loadConnections(): Promise<void> {
    try {
      this.connections = await getConnections();
    } catch {
      this.connections = [];
    }
  }

  /** Set the new search term and reload (the view debounces before calling this). */
  async search(q: string): Promise<void> {
    this.query = q;
    await this.load();
  }

  /**
   * Switch between "My Recipes" (null) and a connected household. Resets the
   * search + favorites filter (mirrors the Blazor OnHouseholdSelectionChanged),
   * then reloads.
   */
  async setConnected(id: number | null): Promise<void> {
    this.selectedConnectedId = id;
    this.query = '';
    this.showFavoritesOnly = false;
    await this.load();
  }

  toggleFavoritesFilter(): void {
    this.showFavoritesOnly = !this.showFavoritesOnly;
  }

  /**
   * Toggle a favorite (own household only). Optimistic Set toggle, then POST and adopt the server's
   * answer; on failure the heart flips back (and a 409/4xx refetches).
   */
  async toggleFavorite(id: number): Promise<void> {
    if (this.selectedConnectedId != null) return;
    const had = this.favoriteIds.has(id);
    await this.#resource.mutate({
      key: `fav:${id}`,
      optimistic: () => {
        this.favoriteIds = toggled(this.favoriteIds, id, !had);
        return () => {
          this.favoriteIds = toggled(this.favoriteIds, id, had);
        };
      },
      call: () => apiToggleFavorite(id),
      commit: (res) => {
        this.favoriteIds = toggled(this.favoriteIds, id, res.isFavorite);
      },
      messages: {
        conflict: 'That recipe changed — the list was refreshed.',
        rejected: 'That recipe changed — the list was refreshed.',
        failed: "Couldn't update that favorite right now.",
      },
    });
  }

  /**
   * Delete a recipe (own household only). Optimistic removal from the grid, then DELETE. On failure
   * the ONE card goes back at its index (an item-scoped undo: a whole-array restore would also erase
   * what other writes did meanwhile); a 409/4xx also refetches the truth.
   */
  async deleteRecipe(id: number): Promise<void> {
    const outcome = await this.#resource.mutate({
      key: `del:${id}`,
      optimistic: () => {
        const index = this.recipes.findIndex((r) => r.recipeId === id);
        if (index < 0) return;
        const removed = this.recipes[index];
        this.recipes = this.recipes.filter((r) => r.recipeId !== id);
        return () => {
          if (this.recipes.some((r) => r.recipeId === id)) return;
          const restored = [...this.recipes];
          restored.splice(Math.min(index, restored.length), 0, removed);
          this.recipes = restored;
        };
      },
      call: () => apiDeleteRecipe(id),
      messages: {
        conflict: 'That recipe changed — the list was refreshed.',
        rejected: 'That recipe changed — the list was refreshed.',
        failed: "Couldn't delete that recipe right now.",
      },
    });
    // The success toast sits outside the write window, so it can never take a failure path.
    if (outcome === 'ok') showToast({ message: 'Recipe deleted.', kind: 'success' });
  }
}

/** Return a NEW Set with `id` present (`on`) or absent — keeps the $state reassignment reactive. */
function toggled(set: ReadonlySet<number>, id: number, on: boolean): Set<number> {
  const next = new Set(set);
  if (on) next.add(id);
  else next.delete(id);
  return next;
}

/** The single shared list-view store instance (export the instance, not the runes). */
export const recipeListStore = new RecipeListStore();
