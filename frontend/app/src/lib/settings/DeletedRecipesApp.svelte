<script lang="ts">
  // Deleted Recipes view (/settings/deleted-recipes): the household's deleted
  // recipes, most recently deleted first, each with a Restore action. The same
  // shape as the "Deleted Categories" card on the Categories page. A restored
  // recipe goes back to Recipes, search and the meal plan with its ingredients.
  import { onMount } from 'svelte';
  import { resolve } from '$app/paths';
  import type { ShellContext } from './lib/types';
  import { deletedRecipesStore } from './lib/deletedRecipesStore.svelte';
  import { formatDeletedDate } from './lib/dates';
  import { recipeTypeLabel } from '#lib/api/recipeType.ts';

  // ctx is provided by the route; the store calls /api directly (household resolved server-side).
  let { ctx }: { ctx: ShellContext } = $props();

  const store = deletedRecipesStore;

  // One-time load — onMount is not reactive, so it can't form the setup-effect fetch loop.
  onMount(() => {
    void ctx; // host context available; not needed by the store
    void store.load();
  });
</script>

<div class="set-page">
  <h1 class="set-title">Deleted Recipes</h1>
  <p class="set-subtitle">
    Recipes your household deleted. Restore one to put it back in
    <a class="set-link" href={resolve('/recipes')}>Recipes</a>, search and the meal plan, with its ingredients.
  </p>

  {#if store.loading}
    <div class="set-skeleton">Loading…</div>
  {:else if store.error}
    <div class="set-error" role="alert">
      <span>{store.error}</span>
      <button type="button" class="set-btn-text" onclick={() => store.load()}>Retry</button>
    </div>
  {:else if store.recipes.length === 0}
    <section class="set-card">
      <div class="set-empty">No deleted recipes.</div>
    </section>
  {:else}
    <section class="set-card">
      <ul class="set-list" aria-label="Deleted recipes">
        {#each store.recipes as recipe (recipe.recipeId)}
          {@const deletedOn = formatDeletedDate(recipe.deletedAt)}
          <li class="set-row">
            <div class="set-row-main">
              <span class="set-name">{recipe.name}</span>
              <span class="set-chip">{recipeTypeLabel(recipe.recipeType)}</span>
              {#if deletedOn}
                <span class="set-deleted-note">(deleted {deletedOn})</span>
              {/if}
            </div>
            <button
              type="button"
              class="set-btn-text"
              aria-label={`Restore ${recipe.name}`}
              onclick={() => store.restore(recipe)}
            >
              Restore
            </button>
          </li>
        {/each}
      </ul>
    </section>
  {/if}
</div>

<style>
  .set-page {
    max-width: 720px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .set-title {
    margin: 0 0 4px;
    font-size: 1.5rem;
    font-weight: 500;
  }
  .set-subtitle {
    margin: 0 0 20px;
    color: var(--color-text-muted);
    font-size: 0.875rem;
  }
  .set-link {
    color: var(--color-primary);
  }
  .set-card {
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-1);
    padding: 16px;
    margin-bottom: 16px;
  }
  .set-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .set-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 10px 12px;
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-sm);
  }
  .set-row-main {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 6px 10px;
    min-width: 0;
  }
  .set-name {
    overflow-wrap: anywhere;
  }
  .set-chip {
    font-size: 0.6875rem;
    font-weight: 500;
    padding: 2px 10px;
    border-radius: 12px;
    white-space: nowrap;
    border: 1px solid var(--color-line-strong);
    color: var(--color-text-muted);
  }
  .set-deleted-note {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .set-btn-text {
    font: inherit;
    font-weight: 500;
    background: transparent;
    border: none;
    color: var(--color-primary);
    cursor: pointer;
    padding: 6px 10px;
    border-radius: var(--radius-sm);
    flex-shrink: 0;
  }
  .set-btn-text:hover {
    background: var(--color-action-hover);
  }
  .set-empty,
  .set-skeleton,
  .set-error {
    padding: 16px;
    border-radius: var(--radius-sm);
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .set-error {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    background: rgba(229, 57, 53, 0.08);
    border-left: 4px solid var(--color-error);
    color: var(--color-text);
  }
</style>
