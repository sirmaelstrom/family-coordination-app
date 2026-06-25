// ─────────────────────────────────────────────────────────────────────────
// List-view store — the source of truth for the recipes grid (#recipes-list-root).
//
// ⚠ Svelte 5 rune rule (global CORRECTION): never `export` a reassigned
// `$state`/`$derived` from a module. We wrap the mutable state in a class
// instance and export the instance — the runes live as fields on the object.
//
// Parity-first ⇒ versionless / last-write-wins. Search is SERVER-side + debounced
// (D9); the favorites filter is CLIENT-side over the loaded set; favoriteRecipeIds
// arrive with the same payload (one round-trip). On any 4xx/network error we
// reconcile (re-run the current query) + a calm toast. Liveness re-runs the
// current query while the tab is visible.
//
// Owns ONLY list-view files (spec §6 store split) — the edit view has its own
// store (recipeEditStore.svelte.ts), so WP-5 / WP-6 never collide.
// ─────────────────────────────────────────────────────────────────────────

import type {
  ConnectedHouseholdDto,
  RecipeListItemDto,
  ShellContext,
} from './types';
import {
  ApiError,
  listRecipes,
  listConnectedRecipes,
  toggleFavorite as apiToggleFavorite,
  deleteRecipe as apiDeleteRecipe,
  getConnections,
} from './api';
import { showToast } from './toasts.svelte';

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
  /** Monotonic load id — search / connected-switch / liveness / reconcile all call load(); a slower
   *  older response must NOT overwrite a newer one, so each load only applies if it's still the latest. */
  private loadSeq = 0;
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
    const seq = ++this.loadSeq;
    try {
      if (!this.hasLoaded) this.loading = true;
      this.error = null;
      if (this.selectedConnectedId == null) {
        const dto = await listRecipes(this.query);
        if (seq !== this.loadSeq) return; // a newer load superseded this one
        this.recipes = dto.recipes;
        this.favoriteIds = new Set(dto.favoriteRecipeIds);
      } else {
        const dto = await listConnectedRecipes(this.selectedConnectedId, this.query);
        if (seq !== this.loadSeq) return;
        this.recipes = dto.recipes;
        this.favoriteIds = new Set();
      }
      this.hasLoaded = true;
    } catch (e) {
      if (seq !== this.loadSeq) return; // don't surface an error from a superseded load
      this.error =
        e instanceof ApiError
          ? `Failed to load recipes (HTTP ${e.status}).`
          : e instanceof Error
            ? e.message
            : String(e);
    } finally {
      if (seq === this.loadSeq) this.loading = false;
    }
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
   * Toggle a favorite (own household only). Optimistic Set toggle, then POST and
   * reconcile to the server's authoritative state; on error revert + (4xx) refetch.
   */
  async toggleFavorite(id: number): Promise<void> {
    if (this.selectedConnectedId != null) return;
    const had = this.favoriteIds.has(id);
    this.favoriteIds = toggled(this.favoriteIds, id, !had);
    try {
      const res = await apiToggleFavorite(id);
      this.favoriteIds = toggled(this.favoriteIds, id, res.isFavorite);
    } catch (e) {
      this.favoriteIds = toggled(this.favoriteIds, id, had); // revert
      if (e instanceof ApiError) {
        await this.reconcile();
        showToast({ message: 'That recipe changed — the list was refreshed.', kind: 'info' });
      } else {
        showToast({ message: "Couldn't update that favorite right now.", kind: 'error' });
      }
    }
  }

  /**
   * Delete a recipe (own household only). Optimistic removal from the grid, then
   * DELETE; on a 4xx (incl. the empty-400-from-404 quirk) reconcile to truth, on
   * network/5xx restore the card.
   */
  async deleteRecipe(id: number): Promise<void> {
    const prev = this.recipes;
    this.recipes = this.recipes.filter((r) => r.recipeId !== id);
    try {
      await apiDeleteRecipe(id);
      showToast({ message: 'Recipe deleted.', kind: 'success' });
    } catch (e) {
      if (e instanceof ApiError) {
        await this.reconcile();
        showToast({ message: 'That recipe changed — the list was refreshed.', kind: 'info' });
      } else {
        this.recipes = prev;
        showToast({ message: "Couldn't delete that recipe right now.", kind: 'error' });
      }
    }
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
