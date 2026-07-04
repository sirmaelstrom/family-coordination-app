<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // One meal slot (a day × meal-type cell). Mirrors the Blazor MealSlot:
  //   • EMPTY  → a dashed box with a ＋ that opens the picker.
  //   • ENTRIES → a card per entry:
  //       – recipe: image (or a book placeholder) + name + a type chip
  //         (hidden for `main`, matching the Blazor RecipeType.Main guard);
  //         clicking it opens the recipe detail sheet.
  //       – custom meal: a restaurant icon + name + notes; clicking opens the
  //         custom-meal notes view (RecipeDetailSheet's custom mode).
  //     each entry has a × remove; below the entries an "add side/dessert" row
  //     re-opens the picker for the same slot.
  // ───────────────────────────────────────────────────────────────────────
  import type { MealPlanEntryDto } from '../types';
  import { recipeTypeLabel } from '../recipeType';

  interface Props {
    entries: MealPlanEntryDto[];
    /** Open the picker for this slot (＋ / add side-dessert). */
    onAdd: () => void;
    /** Remove an entry (confirms in the parent). */
    onRemove: (entry: MealPlanEntryDto) => void;
    /** View a recipe entry's detail. */
    onViewRecipe: (entry: MealPlanEntryDto) => void;
    /** View a custom-meal entry's notes. */
    onViewCustom: (entry: MealPlanEntryDto) => void;
  }

  let { entries, onAdd, onRemove, onViewRecipe, onViewCustom }: Props = $props();

  let hasEntries = $derived(entries.length > 0);
</script>

{#if !hasEntries}
  <button type="button" class="mp-slot mp-slot-empty" aria-label="Add a meal" onclick={onAdd}>
    <svg viewBox="0 0 24 24" width="28" height="28" aria-hidden="true">
      <path d="M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z" fill="currentColor" />
    </svg>
  </button>
{:else}
  <div class="mp-slot mp-slot-filled">
    <div class="mp-slot-entries">
      {#each entries as entry (entry.entryId)}
        {#if entry.recipe}
          <div class="mp-entry">
            <button
              type="button"
              class="mp-entry-main"
              onclick={() => onViewRecipe(entry)}
              title={entry.recipe.name}
            >
              {#if entry.recipe.imagePath}
                <img src={entry.recipe.imagePath} alt={entry.recipe.name} class="mp-entry-image" />
              {:else}
                <span class="mp-entry-placeholder" aria-label="Recipe (no image)">
                  <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
                    <path
                      d="M18 2H6c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 18H6V4h7v7l2.5-1.5L17 11V4h1v16z"
                      fill="currentColor"
                    />
                  </svg>
                </span>
              {/if}
              <span class="mp-entry-details">
                <span class="mp-entry-name">{entry.recipe.name}</span>
                {#if entry.recipe.recipeType !== 'main'}
                  <span class="mp-entry-chip">{recipeTypeLabel(entry.recipe.recipeType)}</span>
                {/if}
              </span>
            </button>
            <button
              type="button"
              class="mp-entry-remove"
              aria-label="Remove this meal"
              onclick={() => onRemove(entry)}
            >
              <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
                <path
                  d="M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"
                  fill="currentColor"
                />
              </svg>
            </button>
          </div>
        {:else if entry.customMealName}
          <div class="mp-entry">
            <button
              type="button"
              class="mp-entry-main"
              onclick={() => onViewCustom(entry)}
              title={entry.notes ? `${entry.customMealName}\n\nNotes: ${entry.notes}` : entry.customMealName}
            >
              <span class="mp-entry-custom-icon" aria-hidden="true">
                <svg viewBox="0 0 24 24" width="18" height="18">
                  <path
                    d="M11 9H9V2H7v7H5V2H3v7c0 2.12 1.66 3.84 3.75 3.97V22h2.5v-9.03C11.34 12.84 13 11.12 13 9V2h-2v7zm5-3v8h2.5v8H21V2c-2.76 0-5 2.24-5 4z"
                    fill="currentColor"
                  />
                </svg>
              </span>
              <span class="mp-entry-details">
                <span class="mp-entry-name">{entry.customMealName}</span>
                {#if entry.notes}
                  <span class="mp-entry-notes">{entry.notes}</span>
                {/if}
              </span>
            </button>
            <button
              type="button"
              class="mp-entry-remove"
              aria-label="Remove this meal"
              onclick={() => onRemove(entry)}
            >
              <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
                <path
                  d="M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"
                  fill="currentColor"
                />
              </svg>
            </button>
          </div>
        {/if}
      {/each}

      <button type="button" class="mp-slot-add-more" onclick={onAdd}>
        <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
          <path d="M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z" fill="currentColor" />
        </svg>
        Add side/dessert
      </button>
    </div>
  </div>
{/if}

<style>
  .mp-slot {
    min-height: 80px;
    width: 100%;
    border-radius: var(--radius-sm);
  }

  .mp-slot-empty {
    display: grid;
    place-items: center;
    border: 2px dashed var(--color-line-strong);
    background: transparent;
    color: var(--color-text-muted);
    cursor: pointer;
    padding: 0;
    transition: background-color 0.2s ease;
  }
  .mp-slot-empty:hover {
    background: var(--color-action-hover);
  }

  .mp-slot-filled {
    background: var(--color-surface);
    box-shadow: var(--shadow-2);
  }
  .mp-slot-entries {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 8px;
  }

  .mp-entry {
    display: flex;
    align-items: center;
    gap: 4px;
    border-radius: var(--radius-sm);
    position: relative;
    transition: background-color 0.2s ease;
  }
  .mp-entry:hover {
    background: var(--color-action-hover);
  }

  .mp-entry-main {
    display: flex;
    align-items: center;
    gap: 8px;
    flex: 1;
    min-width: 0;
    text-align: left;
    font: inherit;
    color: inherit;
    background: transparent;
    border: none;
    padding: 6px 8px;
    border-radius: var(--radius-sm);
    cursor: pointer;
  }

  .mp-entry-image,
  .mp-entry-placeholder,
  .mp-entry-custom-icon {
    width: 40px;
    height: 40px;
    border-radius: var(--radius-sm);
    flex-shrink: 0;
  }
  .mp-entry-image {
    object-fit: cover;
  }
  .mp-entry-placeholder {
    display: grid;
    place-items: center;
    background: var(--color-secondary);
    color: #fff;
  }
  .mp-entry-custom-icon {
    display: grid;
    place-items: center;
    background: var(--color-primary);
    color: #fff;
  }

  .mp-entry-details {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .mp-entry-name {
    line-height: 1.2;
    font-size: 0.875rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .mp-entry-chip {
    align-self: flex-start;
    font-size: 0.65rem;
    line-height: 1;
    padding: 3px 6px;
    border-radius: 9px;
    color: var(--color-text-muted);
    border: 1px solid var(--color-line);
  }
  .mp-entry-notes {
    font-size: 0.75rem;
    font-style: italic;
    color: var(--color-text-muted);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .mp-entry-remove {
    flex-shrink: 0;
    display: grid;
    place-items: center;
    width: 32px;
    height: 32px;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    border-radius: var(--radius-sm);
    cursor: pointer;
    opacity: 0;
    transition: opacity 0.2s ease, color 0.15s, background-color 0.15s;
  }
  .mp-entry:hover .mp-entry-remove,
  .mp-entry-remove:focus-visible {
    opacity: 1;
  }
  .mp-entry-remove:hover {
    color: var(--color-error);
    background: var(--color-action-hover);
  }

  .mp-slot-add-more {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 4px;
    font: inherit;
    font-size: 0.75rem;
    color: var(--color-text-muted);
    padding: 6px;
    border: 1px dashed var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: transparent;
    cursor: pointer;
    margin-top: 4px;
    opacity: 0.6;
    transition: opacity 0.2s ease, background-color 0.15s;
  }
  .mp-slot-add-more:hover {
    opacity: 1;
    background: var(--color-action-hover);
  }
</style>
