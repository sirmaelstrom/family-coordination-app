<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Root of the recipes LIST view (#recipes-list-root, data-view="list").
  // Reads ShellContext from the root data-attrs, loads the grid into the shared
  // list store, and renders the connected-household selector, search + favorites,
  // the card grid, the detail drawer, the import + delete dialogs, and toasts.
  // Liveness re-runs the current query while the tab is visible (collaboration).
  //
  // ⚠ Loop safety (spec §10): the one-time setup $effect is wrapped ENTIRELY in
  // untrack() — load()/loadConnections() read then write store $state, so without
  // untrack the effect would subscribe to the state it writes → an infinite
  // fetch→write→re-run loop. untrack gives it zero reactive deps (runs once);
  // liveness + user actions drive every later refresh.
  // ───────────────────────────────────────────────────────────────────────
  import { untrack } from 'svelte';
  import { base } from '$app/paths';
  import { goto } from '$app/navigation';
  import type { RecipeListItemDto, ShellContext } from './lib/types';
  import { recipeListStore } from './lib/recipeListStore.svelte';
  import { copyConnectedRecipe } from './lib/api';
  import { startLiveness, type LivenessHandle } from './lib/liveness';
  import { showToast } from '$lib/shared/toast-store.svelte';
  import RecipeCard from './lib/components/RecipeCard.svelte';
  import ConnectedHouseholdSelector from './lib/components/ConnectedHouseholdSelector.svelte';
  import RecipeDetailDrawer from './lib/components/RecipeDetailDrawer.svelte';
  import ImportDialog from './lib/components/ImportDialog.svelte';
  import ConfirmDialog from '$lib/shared/ConfirmDialog.svelte';

  interface Props {
    ctx: ShellContext;
    /** When true (the /recipes/import route), the import dialog opens on mount. */
    openImport?: boolean;
    /** Called when the import dialog closes — the /recipes/import route navigates back to /recipes. */
    onImportClose?: () => void;
  }

  let { ctx, openImport = false, onImportClose }: Props = $props();

  const store = recipeListStore;

  let liveness: LivenessHandle | null = null;

  // Search is debounced locally (300ms) before hitting the server (mirrors the
  // Blazor MudTextField DebounceInterval). Kept separate from store.query so the
  // input stays responsive between debounce ticks.
  let searchValue = $state('');
  let searchTimer: ReturnType<typeof setTimeout> | null = null;

  // Drawer / dialogs.
  let drawerOpen = $state(false);
  let drawerRecipe = $state<RecipeListItemDto | null>(null);
  let importOpen = $state(openImport);
  let deleteOpen = $state(false);
  let deleteTarget = $state<RecipeListItemDto | null>(null);

  async function load(): Promise<void> {
    await store.load();
  }

  function onSearchInput(value: string): void {
    searchValue = value;
    if (searchTimer) clearTimeout(searchTimer);
    searchTimer = setTimeout(() => void store.search(searchValue), 300);
  }

  async function onSelectHousehold(id: number | null): Promise<void> {
    searchValue = '';
    closeDrawer();
    await store.setConnected(id);
  }

  function openDrawer(recipe: RecipeListItemDto): void {
    drawerRecipe = recipe;
    drawerOpen = true;
  }

  function closeDrawer(): void {
    drawerOpen = false;
    // Keep drawerRecipe so the panel content doesn't flash during the close slide.
  }

  function handleEdit(recipeId: number): void {
    void goto(`${base}/recipes/edit/${recipeId}`);
  }

  function handleAddRecipe(): void {
    void goto(`${base}/recipes/new`);
  }

  function handleAddToMealPlan(name: string): void {
    showToast({ message: `Add '${name}' to meal plan — feature in development.`, kind: 'info' });
  }

  async function handleCopy(recipeId: number): Promise<void> {
    if (store.selectedConnectedId == null) return;
    try {
      await copyConnectedRecipe(store.selectedConnectedId, recipeId);
      showToast({ message: "Recipe copied! It's now in your collection.", kind: 'success' });
    } catch {
      showToast({ message: "Couldn't copy that recipe right now.", kind: 'error' });
    }
  }

  function requestDelete(recipe: RecipeListItemDto): void {
    deleteTarget = recipe;
    deleteOpen = true;
  }

  async function confirmDelete(): Promise<void> {
    const target = deleteTarget;
    deleteOpen = false;
    deleteTarget = null;
    if (!target) return;
    closeDrawer();
    await store.deleteRecipe(target.recipeId);
  }

  function onImported(recipeId: number): void {
    void goto(`${base}/recipes/edit/${recipeId}`);
  }

  // Empty-state copy mirrors Recipes.razor:120-195.
  let emptyTitle = $derived(
    store.isViewingConnected
      ? 'No shared recipes'
      : store.showFavoritesOnly
        ? 'No favorites yet'
        : 'No recipes yet',
  );
  let emptyHint = $derived(
    store.isViewingConnected
      ? store.query.trim()
        ? `No recipes found matching "${store.query}"`
        : "This family hasn't added any recipes yet"
      : store.showFavoritesOnly
        ? 'Tap the heart on a recipe to add it to your favorites'
        : store.query.trim()
          ? `No recipes found matching "${store.query}"`
          : 'Start building your family recipe collection',
  );

  $effect(() => {
    untrack(() => {
      store.init(ctx);
      store.setRefresh(load);
      load(); // reads then writes store $state — MUST be inside untrack
      store.loadConnections();
      // Liveness: ~20s poll while visible + immediate refetch on refocus.
      liveness = startLiveness(() => store.reconcile());
    });

    return () => {
      liveness?.stop();
      liveness = null;
      if (searchTimer) clearTimeout(searchTimer);
      searchTimer = null;
    };
  });
</script>

<div class="rc-container">
  <header class="rc-header">
    <h1 class="rc-title">Recipes</h1>
    {#if !store.isViewingConnected}
      <div class="rc-header-actions">
        <button type="button" class="rc-btn-outline" onclick={() => (importOpen = true)}>
          Import from URL
        </button>
        <button type="button" class="rc-btn-solid" onclick={handleAddRecipe}>Add Recipe</button>
      </div>
    {/if}
  </header>

  {#if store.connections.length > 0}
    <ConnectedHouseholdSelector
      connections={store.connections}
      selectedId={store.selectedConnectedId}
      onSelect={onSelectHousehold}
    />
  {/if}

  <div class="rc-controls">
    <input
      type="search"
      class="rc-search"
      placeholder="Search by name, ingredients, or type"
      value={searchValue}
      oninput={(e) => onSearchInput(e.currentTarget.value)}
    />
    {#if !store.isViewingConnected}
      <button
        type="button"
        class="rc-fav-chip"
        class:rc-fav-chip-on={store.showFavoritesOnly}
        aria-pressed={store.showFavoritesOnly}
        onclick={() => store.toggleFavoritesFilter()}
      >
        {store.showFavoritesOnly ? '♥' : '♡'} Favorites
      </button>
    {/if}
  </div>

  {#if store.error}
    <div class="rc-inline-error" role="alert">
      <span>{store.error}</span>
      <button type="button" class="rc-retry" onclick={load}>Retry</button>
    </div>
  {/if}

  {#if store.loading}
    <div class="rc-grid">
      {#each Array(6) as _, i (i)}
        <div class="rc-skeleton"></div>
      {/each}
    </div>
  {:else if store.displayed.length === 0}
    <div class="rc-empty">
      <h2 class="rc-empty-title">{emptyTitle}</h2>
      <p class="rc-empty-hint">{emptyHint}</p>
      {#if !store.isViewingConnected && !store.showFavoritesOnly && !store.query.trim()}
        <button type="button" class="rc-btn-solid" onclick={handleAddRecipe}>Add Your First Recipe</button>
      {/if}
    </div>
  {:else}
    <div class="rc-grid">
      {#each store.displayed as recipe (recipe.recipeId)}
        <RecipeCard
          {recipe}
          isFavorite={!store.isViewingConnected && store.favoriteIds.has(recipe.recipeId)}
          isReadOnly={store.isViewingConnected}
          sharedFromName={store.isViewingConnected ? store.selectedConnectedName : null}
          onClick={openDrawer}
          onToggleFavorite={(r) => store.toggleFavorite(r.recipeId)}
        />
      {/each}
    </div>
  {/if}
</div>

<RecipeDetailDrawer
  open={drawerOpen}
  summary={drawerRecipe}
  connectedId={store.selectedConnectedId}
  isReadOnly={store.isViewingConnected}
  connectedName={store.selectedConnectedName}
  onClose={closeDrawer}
  onEdit={handleEdit}
  onDelete={requestDelete}
  onCopy={handleCopy}
  onAddToMealPlan={handleAddToMealPlan}
/>

<ImportDialog
  open={importOpen}
  onClose={() => {
    importOpen = false;
    onImportClose?.();
  }}
  {onImported}
/>

<ConfirmDialog
  open={deleteOpen}
  danger
  title="Delete Recipe"
  message={deleteTarget
    ? `Are you sure you want to delete "${deleteTarget.name}"? This action cannot be undone.`
    : ''}
  onCancel={() => {
    deleteOpen = false;
    deleteTarget = null;
  }}
  onConfirm={confirmDelete}
/>

<style>
  .rc-container {
    max-width: 1280px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .rc-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    flex-wrap: wrap;
    margin-bottom: 16px;
  }
  .rc-title {
    margin: 0;
    font-size: 2.125rem;
    font-weight: 400;
    color: var(--color-text);
  }
  .rc-header-actions {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
  }
  .rc-controls {
    display: flex;
    gap: 12px;
    align-items: center;
    flex-wrap: wrap;
    margin-bottom: 16px;
  }
  .rc-search {
    flex: 1;
    min-width: 200px;
    font: inherit;
    padding: 10px 14px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    color: var(--color-text);
  }
  .rc-search:focus {
    outline: none;
    border-color: var(--color-primary);
  }
  .rc-fav-chip {
    font: inherit;
    font-size: 0.8125rem;
    padding: 8px 16px;
    border-radius: 18px;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    color: var(--color-text);
    cursor: pointer;
  }
  .rc-fav-chip:hover {
    background: var(--color-action-hover);
  }
  .rc-fav-chip-on {
    background: var(--color-error);
    border-color: var(--color-error);
    color: #fff;
  }
  .rc-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
    gap: 16px;
    align-items: stretch;
  }
  .rc-skeleton {
    height: 320px;
    border-radius: var(--radius-md);
    background: linear-gradient(
      90deg,
      var(--color-action-hover) 25%,
      var(--color-divider) 37%,
      var(--color-action-hover) 63%
    );
    background-size: 400% 100%;
    animation: rc-shimmer 1.4s ease infinite;
  }
  @keyframes rc-shimmer {
    0% {
      background-position: 100% 50%;
    }
    100% {
      background-position: 0 50%;
    }
  }
  .rc-empty {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    text-align: center;
    min-height: 360px;
    gap: 8px;
  }
  .rc-empty-title {
    margin: 0;
    font-size: 1.5rem;
    font-weight: 400;
  }
  .rc-empty-hint {
    margin: 0 0 12px;
    color: var(--color-text-muted);
  }
  .rc-inline-error {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    padding: 10px 16px;
    margin-bottom: 16px;
    background: rgba(229, 57, 53, 0.08);
    border-left: 4px solid var(--color-error);
    border-radius: var(--radius-sm);
    color: var(--color-error);
    font-size: 0.875rem;
  }
  .rc-retry {
    font: inherit;
    border: none;
    cursor: pointer;
    border-radius: var(--radius-sm);
    padding: 4px 12px;
    background: transparent;
    color: var(--color-error);
    font-weight: 500;
  }
  .rc-retry:hover {
    background: rgba(229, 57, 53, 0.12);
  }
  .rc-btn-solid,
  .rc-btn-outline {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    min-height: 40px;
    font-weight: 500;
  }
  .rc-btn-solid {
    border: none;
    background: var(--color-primary);
    color: #fff;
  }
  .rc-btn-solid:hover {
    background: var(--color-primary-hover);
  }
  .rc-btn-outline {
    border: 1px solid var(--color-secondary);
    background: transparent;
    color: var(--color-secondary);
  }
  .rc-btn-outline:hover {
    background: var(--color-action-hover);
  }
</style>
