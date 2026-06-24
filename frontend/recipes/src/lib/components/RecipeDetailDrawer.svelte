<script lang="ts">
  // Read-only detail slide-out (mirrors the Blazor MudDrawer at Recipes.razor:223-416).
  // Lazy-fetches the full recipe on open — getRecipe (#2) for own recipes,
  // getConnectedRecipe (#16) in connected mode. Hosts the live servings scaler
  // (client-side rescale via quantity.ts), instructions via {@html} (server-
  // sanitized), source link (safe-url guarded), and the action buttons.
  import type { RecipeFullDto, RecipeIngredientFullDto, RecipeListItemDto } from '../types';
  import { getRecipe, getConnectedRecipe, ApiError } from '../api';
  import { initials } from '../avatar';
  import {
    formatScaledQuantity,
    getScaledQuantity,
    getScalingFactor,
    getScalingLabel,
  } from '../quantity';

  interface Props {
    open: boolean;
    /** The card the drawer was opened from (fallback title + delete name). */
    summary: RecipeListItemDto | null;
    /** Connected household id when viewing a connected recipe (else null). */
    connectedId: number | null;
    isReadOnly: boolean;
    connectedName: string | null;
    onClose: () => void;
    onEdit: (recipeId: number) => void;
    onDelete: (summary: RecipeListItemDto) => void;
    onCopy: (recipeId: number) => Promise<void>;
    onAddToMealPlan: (name: string) => void;
  }

  let {
    open,
    summary,
    connectedId,
    isReadOnly,
    connectedName,
    onClose,
    onEdit,
    onDelete,
    onCopy,
    onAddToMealPlan,
  }: Props = $props();

  let detail = $state<RecipeFullDto | null>(null);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let scaledServings = $state(1);
  let copying = $state(false);
  // The `${connectedId}:${recipeId}` we last fetched — reopening the same recipe reuses it.
  let loadedKey: string | null = null;

  function keyFor(s: RecipeListItemDto): string {
    return `${connectedId ?? ''}:${s.recipeId}`;
  }

  async function load(s: RecipeListItemDto): Promise<void> {
    loading = true;
    error = null;
    try {
      const full =
        connectedId == null
          ? await getRecipe(s.recipeId)
          : await getConnectedRecipe(connectedId, s.recipeId);
      detail = full;
      scaledServings = full.servings ?? 1;
      loadedKey = keyFor(s);
    } catch (e) {
      error =
        e instanceof ApiError
          ? `Couldn't load this recipe (HTTP ${e.status}).`
          : "Couldn't load this recipe right now.";
    } finally {
      loading = false;
    }
  }

  // Reactive lazy-load on open / recipe change (mirrors meal-plan RecipeDetailSheet).
  // Guarded by the key so it settles after one load — NOT an unbounded loop.
  $effect(() => {
    if (open && summary && keyFor(summary) !== loadedKey) {
      detail = null;
      void load(summary);
    }
  });

  let factor = $derived(detail ? getScalingFactor(detail.servings, scaledServings) : 1);
  let title = $derived(detail?.name ?? summary?.name ?? '');
  let hasTimeMeta = $derived(
    detail != null && (detail.prepTimeMinutes != null || detail.cookTimeMinutes != null),
  );

  function ingredientLine(ing: RecipeIngredientFullDto): string {
    const parts: string[] = [];
    if (ing.quantity != null) {
      parts.push(formatScaledQuantity(getScaledQuantity(ing.quantity, factor)));
    }
    if (ing.unit) parts.push(ing.unit);
    parts.push(ing.name);
    if (ing.notes) parts.push(`(${ing.notes})`);
    return parts.join(' ');
  }

  function isSafeUrl(url: string | null): boolean {
    if (!url) return false;
    try {
      const u = new URL(url);
      return u.protocol === 'http:' || u.protocol === 'https:';
    } catch {
      return false;
    }
  }

  function increaseServings(): void {
    scaledServings++;
  }
  function decreaseServings(): void {
    if (scaledServings > 1) scaledServings--;
  }
  function resetServings(): void {
    scaledServings = detail?.servings ?? 1;
  }

  async function handleCopy(): Promise<void> {
    if (!detail) return;
    copying = true;
    try {
      await onCopy(detail.recipeId);
    } finally {
      copying = false;
    }
  }
</script>

{#if open}
  <button type="button" class="rc-drawer-scrim" aria-label="Close panel" onclick={onClose}></button>
{/if}

<aside class="rc-drawer" class:rc-drawer-open={open} aria-hidden={!open}>
  <header class="rc-drawer-head">
    <h2 class="rc-drawer-title">{title}</h2>
    <button type="button" class="rc-drawer-x" aria-label="Close panel" onclick={onClose}>×</button>
  </header>

  <div class="rc-drawer-body">
    {#if loading}
      <div class="rc-drawer-loading">Loading recipe…</div>
    {:else if error}
      <div class="rc-drawer-error" role="alert">
        <span>{error}</span>
        {#if summary}
          <button type="button" class="rc-retry" onclick={() => summary && load(summary)}>Retry</button>
        {/if}
      </div>
    {:else if detail}
      {#if detail.imagePath}
        <div class="rc-detail-image">
          <img src={detail.imagePath} alt={detail.name} />
        </div>
      {/if}

      <div class="rc-detail-author">
        {#if detail.createdByName}
          {#if detail.createdByPictureUrl}
            <img class="rc-avatar" src={detail.createdByPictureUrl} alt={detail.createdByName} />
          {:else}
            <span class="rc-avatar rc-avatar-initials">{initials(detail.createdByName)}</span>
          {/if}
          <span class="rc-detail-author-name">Added by {detail.createdByName}</span>
        {:else}
          <span class="rc-avatar rc-avatar-initials" aria-hidden="true">∅</span>
          <span class="rc-detail-author-name rc-italic">Added by deleted user</span>
        {/if}
      </div>

      {#if isReadOnly && connectedName}
        <p class="rc-detail-from">From {connectedName}</p>
      {/if}

      {#if hasTimeMeta}
        <div class="rc-detail-chips">
          {#if detail.prepTimeMinutes != null}
            <span class="rc-detail-chip rc-chip-info">Prep: {detail.prepTimeMinutes} min</span>
          {/if}
          {#if detail.cookTimeMinutes != null}
            <span class="rc-detail-chip rc-chip-warn">Cook: {detail.cookTimeMinutes} min</span>
          {/if}
        </div>
      {/if}

      {#if detail.servings != null}
        <div class="rc-scaler">
          <span class="rc-scaler-label">🍽 Servings:</span>
          <button
            type="button"
            class="rc-step"
            aria-label="Decrease servings"
            onclick={decreaseServings}
            disabled={scaledServings <= 1}>−</button
          >
          <strong class="rc-scaler-count">{scaledServings}</strong>
          <button type="button" class="rc-step" aria-label="Increase servings" onclick={increaseServings}>+</button>
          {#if scaledServings !== detail.servings}
            <span class="rc-scaler-badge">{getScalingLabel(factor)}</span>
            <button type="button" class="rc-text-btn" onclick={resetServings}>Reset</button>
          {/if}
        </div>
      {/if}

      {#if detail.description}
        <p class="rc-detail-description">{detail.description}</p>
      {/if}

      {#if detail.ingredients.length > 0}
        <h3 class="rc-detail-subhead">Ingredients</h3>
        <ul class="rc-detail-ingredients">
          {#each [...detail.ingredients].sort((a, b) => a.sortOrder - b.sortOrder) as ing (ing.ingredientId)}
            <li>{ingredientLine(ing)}</li>
          {/each}
        </ul>
      {/if}

      {#if detail.instructionsHtml.trim()}
        <h3 class="rc-detail-subhead">Instructions</h3>
        <!-- Server-sanitized HTML (MarkdownHelper.ToSafeHtml). No client markdown lib. -->
        <div class="rc-detail-instructions">{@html detail.instructionsHtml}</div>
      {/if}

      {#if isSafeUrl(detail.sourceUrl)}
        <div class="rc-detail-source">
          <span aria-hidden="true">🔗</span>
          <a href={detail.sourceUrl} target="_blank" rel="noopener noreferrer">View Original Recipe</a>
        </div>
      {/if}
    {/if}
  </div>

  {#if detail}
    <footer class="rc-drawer-actions">
      {#if isReadOnly}
        <button type="button" class="rc-btn-solid rc-full" onclick={handleCopy} disabled={copying}>
          {copying ? 'Copying…' : 'Copy to My Recipes'}
        </button>
      {:else}
        <button type="button" class="rc-btn-solid rc-full" onclick={() => detail && onEdit(detail.recipeId)}>
          Edit Recipe
        </button>
        <button type="button" class="rc-btn-outline rc-full" onclick={() => detail && onAddToMealPlan(detail.name)}>
          Add to Meal Plan
        </button>
        <button type="button" class="rc-btn-text-danger rc-full" onclick={() => summary && onDelete(summary)}>
          Delete Recipe
        </button>
      {/if}
    </footer>
  {/if}
</aside>

<style>
  .rc-drawer-scrim {
    position: fixed;
    inset: 0;
    border: none;
    padding: 0;
    background: rgba(0, 0, 0, 0.5);
    z-index: 1299;
    cursor: default;
  }
  .rc-drawer {
    position: fixed;
    top: 0;
    right: 0;
    bottom: 0;
    width: min(800px, 90vw);
    background: var(--color-surface);
    color: var(--color-text);
    box-shadow: var(--shadow-4);
    z-index: 1300;
    display: flex;
    flex-direction: column;
    transform: translateX(100%);
    transition: transform 0.25s ease;
    visibility: hidden;
  }
  .rc-drawer-open {
    transform: translateX(0);
    visibility: visible;
  }
  .rc-drawer-head {
    flex-shrink: 0;
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 16px 16px 12px 24px;
    border-bottom: 1px solid var(--color-line);
  }
  .rc-drawer-title {
    flex: 1;
    min-width: 0;
    margin: 0;
    font-size: 1.5rem;
    font-weight: 400;
    overflow-wrap: anywhere;
  }
  .rc-drawer-x {
    flex-shrink: 0;
    width: 40px;
    height: 40px;
    display: grid;
    place-items: center;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    border-radius: var(--radius-sm);
    font-size: 1.5rem;
    line-height: 1;
    cursor: pointer;
  }
  .rc-drawer-x:hover {
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .rc-drawer-body {
    flex: 1;
    overflow-y: auto;
    padding: 16px 24px;
  }
  .rc-detail-image {
    width: 100%;
    height: 250px;
    margin-bottom: 16px;
    border-radius: 8px;
    overflow: hidden;
  }
  .rc-detail-image img {
    width: 100%;
    height: 100%;
    object-fit: cover;
  }
  .rc-detail-author {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 12px;
  }
  .rc-avatar {
    width: 28px;
    height: 28px;
    border-radius: 50%;
    object-fit: cover;
    flex-shrink: 0;
  }
  .rc-avatar-initials {
    display: grid;
    place-items: center;
    background: var(--color-primary-soft);
    color: #fff;
    font-size: 0.6875rem;
    font-weight: 600;
  }
  .rc-detail-author-name {
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .rc-italic {
    font-style: italic;
  }
  .rc-detail-from {
    margin: 0 0 12px;
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .rc-detail-chips {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 16px;
  }
  .rc-detail-chip {
    font-size: 0.8125rem;
    padding: 4px 12px;
    border-radius: 14px;
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .rc-chip-info {
    color: var(--color-info);
    border: 1px solid var(--color-info);
    background: transparent;
  }
  .rc-chip-warn {
    color: var(--color-warning);
    border: 1px solid var(--color-warning);
    background: transparent;
  }
  .rc-scaler {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 16px;
    flex-wrap: wrap;
  }
  .rc-scaler-label {
    font-size: 0.875rem;
    color: var(--color-success);
  }
  .rc-step {
    width: 28px;
    height: 28px;
    display: grid;
    place-items: center;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    color: var(--color-text);
    border-radius: 50%;
    cursor: pointer;
    font-size: 1.1rem;
    line-height: 1;
  }
  .rc-step:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .rc-step:disabled {
    opacity: 0.4;
    cursor: default;
  }
  .rc-scaler-count {
    min-width: 24px;
    text-align: center;
  }
  .rc-scaler-badge {
    font-size: 0.75rem;
    padding: 2px 10px;
    border-radius: 12px;
    border: 1px solid var(--color-info);
    color: var(--color-info);
  }
  .rc-text-btn {
    font: inherit;
    background: transparent;
    border: none;
    color: var(--color-primary);
    cursor: pointer;
    font-size: 0.8125rem;
  }
  .rc-text-btn:hover {
    text-decoration: underline;
  }
  .rc-detail-description {
    margin: 0 0 16px;
    line-height: 1.5;
  }
  .rc-detail-subhead {
    margin: 16px 0 8px;
    font-size: 1.125rem;
    font-weight: 500;
  }
  .rc-detail-ingredients {
    margin: 0 0 16px;
    padding-left: 1.25rem;
    display: flex;
    flex-direction: column;
    gap: 4px;
    font-size: 0.9375rem;
  }
  .rc-detail-instructions {
    line-height: 1.6;
    margin-bottom: 16px;
  }
  .rc-detail-instructions :global(ol),
  .rc-detail-instructions :global(ul) {
    margin-left: 1.5rem;
    margin-bottom: 1rem;
  }
  .rc-detail-instructions :global(li) {
    margin-bottom: 0.5rem;
  }
  .rc-detail-instructions :global(p) {
    margin: 0 0 0.75rem;
  }
  .rc-detail-source {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 16px;
    font-size: 0.875rem;
  }
  .rc-detail-source a {
    color: var(--color-secondary);
  }
  .rc-drawer-loading {
    padding: 48px 0;
    text-align: center;
    color: var(--color-text-muted);
  }
  .rc-drawer-error {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 12px 16px;
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
  .rc-drawer-actions {
    flex-shrink: 0;
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 16px 24px calc(16px + env(safe-area-inset-bottom, 0px));
    border-top: 1px solid var(--color-line);
    background: var(--color-surface);
  }
  .rc-full {
    width: 100%;
  }
  .rc-btn-solid,
  .rc-btn-outline,
  .rc-btn-text-danger {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    min-height: 44px;
    font-weight: 500;
  }
  .rc-btn-solid {
    border: none;
    background: var(--color-primary);
    color: #fff;
  }
  .rc-btn-solid:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .rc-btn-solid:disabled {
    opacity: 0.6;
    cursor: default;
  }
  .rc-btn-outline {
    border: 1px solid var(--color-secondary);
    background: transparent;
    color: var(--color-secondary);
  }
  .rc-btn-outline:hover {
    background: var(--color-action-hover);
  }
  .rc-btn-text-danger {
    border: none;
    background: transparent;
    color: var(--color-error);
  }
  .rc-btn-text-danger:hover {
    background: rgba(229, 57, 53, 0.08);
  }
</style>
