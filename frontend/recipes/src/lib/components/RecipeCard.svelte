<script lang="ts">
  // One recipe card in the grid. Mirrors RecipeCard.razor: image/placeholder,
  // type chip (hidden for `main`), "imported" cloud icon, favorite heart (hidden
  // when read-only/connected), title, author (pic-or-initials, or "Deleted user"),
  // optional connected-household attribution, and the first-3 ingredient preview.
  import type { RecipeListItemDto } from '../types';
  import { recipeTypeShort, recipeTypeColor } from '../recipeType';
  import { initials } from '../avatar';

  interface Props {
    recipe: RecipeListItemDto;
    isFavorite: boolean;
    isReadOnly: boolean;
    sharedFromName: string | null;
    onClick: (recipe: RecipeListItemDto) => void;
    onToggleFavorite: (recipe: RecipeListItemDto) => void;
  }

  let { recipe, isFavorite, isReadOnly, sharedFromName, onClick, onToggleFavorite }: Props = $props();

  let preview = $derived(
    recipe.ingredientCount === 0
      ? null
      : recipe.ingredientPreview.join(', ') +
          (recipe.ingredientCount > 3 ? `, +${recipe.ingredientCount - 3} more` : ''),
  );

  function handleFavorite(e: MouseEvent): void {
    e.stopPropagation(); // don't open the drawer when tapping the heart
    onToggleFavorite(recipe);
  }
</script>

<button type="button" class="rc-card" onclick={() => onClick(recipe)}>
  <div class="rc-card-image">
    <img
      src={recipe.imagePath ?? '/images/recipe-placeholder.svg'}
      alt={recipe.name}
      loading="lazy"
    />
    {#if !isReadOnly}
      <span
        class="rc-fav"
        class:rc-fav-on={isFavorite}
        role="button"
        tabindex="0"
        aria-label={isFavorite ? 'Remove from favorites' : 'Add to favorites'}
        aria-pressed={isFavorite}
        onclick={handleFavorite}
        onkeydown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            handleFavorite(e as unknown as MouseEvent);
          }
        }}
      >
        {isFavorite ? '♥' : '♡'}
      </span>
    {/if}
  </div>

  <div class="rc-card-body">
    <div class="rc-card-title-row">
      <span class="rc-card-title">{recipe.name}</span>
      <span class="rc-card-badges">
        {#if recipe.recipeType !== 'main'}
          <span class="rc-type-chip" style="--chip-color: {recipeTypeColor(recipe.recipeType)}">
            {recipeTypeShort(recipe.recipeType)}
          </span>
        {/if}
        {#if recipe.hasSourceUrl}
          <span class="rc-imported" title="Imported from web" aria-label="Imported from web">☁</span>
        {/if}
      </span>
    </div>

    <div class="rc-card-author">
      {#if recipe.createdByName}
        {#if recipe.createdByPictureUrl}
          <img class="rc-avatar" src={recipe.createdByPictureUrl} alt={recipe.createdByName} />
        {:else}
          <span class="rc-avatar rc-avatar-initials">{initials(recipe.createdByName)}</span>
        {/if}
        <span class="rc-author-name">{recipe.createdByName}</span>
      {:else}
        <span class="rc-avatar rc-avatar-initials" aria-hidden="true">∅</span>
        <span class="rc-author-name rc-author-deleted">Deleted user</span>
      {/if}
    </div>

    {#if sharedFromName}
      <div class="rc-attribution">From {sharedFromName}</div>
    {/if}

    <div class="rc-card-ingredients">
      {#if preview}
        {preview}
      {:else}
        <span class="rc-no-ingredients">No ingredients</span>
      {/if}
    </div>
  </div>
</button>

<style>
  .rc-card {
    font: inherit;
    text-align: left;
    width: 100%;
    height: 100%;
    display: flex;
    flex-direction: column;
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-1);
    cursor: pointer;
    padding: 0;
    overflow: hidden;
    color: var(--color-text);
    transition:
      transform 0.2s ease,
      box-shadow 0.2s ease;
  }
  .rc-card:hover {
    transform: translateY(-2px);
    box-shadow: var(--shadow-4);
  }
  .rc-card-image {
    position: relative;
    flex-shrink: 0;
    height: 200px;
    background: var(--color-action-hover);
  }
  .rc-card-image img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
  }
  .rc-fav {
    position: absolute;
    top: 8px;
    right: 8px;
    display: grid;
    place-items: center;
    width: 34px;
    height: 34px;
    border-radius: 50%;
    background: rgba(255, 255, 255, 0.92);
    color: var(--color-text-muted);
    font-size: 1.1rem;
    line-height: 1;
    cursor: pointer;
    backdrop-filter: blur(4px);
  }
  .rc-fav:hover {
    background: #fff;
  }
  .rc-fav-on {
    color: var(--color-error);
  }
  .rc-card-body {
    display: flex;
    flex-direction: column;
    flex: 1;
    min-height: 120px;
    padding: 16px;
  }
  .rc-card-title-row {
    display: flex;
    align-items: flex-start;
    gap: 8px;
    margin-bottom: 8px;
    min-height: 48px;
  }
  .rc-card-title {
    flex: 1;
    font-size: 1.125rem;
    font-weight: 500;
    line-height: 1.3;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }
  .rc-card-badges {
    display: flex;
    align-items: center;
    gap: 4px;
    flex-shrink: 0;
  }
  .rc-type-chip {
    font-size: 0.6875rem;
    font-weight: 500;
    padding: 2px 8px;
    border-radius: 12px;
    border: 1px solid var(--chip-color);
    color: var(--chip-color);
    white-space: nowrap;
  }
  .rc-imported {
    color: var(--color-secondary);
    font-size: 0.9rem;
  }
  .rc-card-author {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 8px;
  }
  .rc-avatar {
    width: 24px;
    height: 24px;
    border-radius: 50%;
    object-fit: cover;
    flex-shrink: 0;
  }
  .rc-avatar-initials {
    display: grid;
    place-items: center;
    background: var(--color-primary-soft);
    color: #fff;
    font-size: 0.625rem;
    font-weight: 600;
  }
  .rc-author-name {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .rc-author-deleted {
    font-style: italic;
  }
  .rc-attribution {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    margin-bottom: 4px;
  }
  .rc-card-ingredients {
    margin-top: auto;
    font-size: 0.875rem;
    color: var(--color-text-muted);
    display: -webkit-box;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }
  .rc-no-ingredients {
    font-style: italic;
  }
</style>
