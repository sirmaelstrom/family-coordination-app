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
  //
  // Drag-to-assign: the entries list is a svelte-dnd-action zone (one zone per
  // slot — the store owns the per-zone arrays; see state.svelte.ts). The zone
  // wrapper contains ONLY item elements (the library's contract), so the add
  // affordances live OUTSIDE it. An EMPTY slot still renders the zone (an
  // empty per-zone array) stretched over the ＋ button, so it is a live drop
  // target; the zone itself is pointer-transparent (drop detection is
  // coordinate-based) while its item children stay interactive, so tap-to-add,
  // tap-to-view, × remove all keep working. Whole-card drag; touch engages via
  // long-press (delayTouchStart) so scrolling and taps stay natural on phones.
  // ───────────────────────────────────────────────────────────────────────
  import type { MealPlanEntryDto } from '../types';
  import type { DragEntry } from '../board-ops';
  import { recipeTypeLabel } from '../recipeType';
  import { dndzone, SHADOW_ITEM_MARKER_PROPERTY_NAME, type DndEvent } from 'svelte-dnd-action';

  interface Props {
    /**
     * Compact (calendar-grid) layout: the grid's day columns are ~90–250px, so
     * the side-by-side image+name row leaves the name a handful of characters.
     * Compact stacks the card — full-width image banner, name below at full
     * cell width (up to 3 lines). The day list omits it (roomy side-by-side).
     * A class variant, NOT a container query: `container-type` would make the
     * cell the containing block for svelte-dnd-action's position:fixed dragged
     * card (clipped/mispositioned drags).
     */
    compact?: boolean;
    /** The slot's drag rows (entry + the dnd `id`) — the store's per-zone array. */
    entries: DragEntry[];
    /** Open the picker for this slot (＋ / add side-dessert). */
    onAdd: () => void;
    /** Remove an entry (confirms in the parent). */
    onRemove: (entry: MealPlanEntryDto) => void;
    /** View a recipe entry's detail. */
    onViewRecipe: (entry: MealPlanEntryDto) => void;
    /** View a custom-meal entry's notes. */
    onViewCustom: (entry: MealPlanEntryDto) => void;
    /** Forward this zone's dnd events to the store (App wires date/mealType). */
    onDnd: (items: DragEntry[], phase: 'consider' | 'finalize') => void;
  }

  let { compact = false, entries, onAdd, onRemove, onViewRecipe, onViewCustom, onDnd }: Props = $props();

  // REAL entries only: while a drag hovers an empty slot the zone briefly holds
  // the library's shadow placeholder — that must NOT flip the slot to its
  // "filled" layout mid-drag (the geometry change would jitter the drop).
  let hasEntries = $derived(
    entries.some((e) => !(e as unknown as Record<string, unknown>)[SHADOW_ITEM_MARKER_PROPERTY_NAME]),
  );

  function handleConsider(e: CustomEvent<DndEvent<DragEntry>>): void {
    onDnd(e.detail.items, 'consider');
  }
  function handleFinalize(e: CustomEvent<DndEvent<DragEntry>>): void {
    onDnd(e.detail.items, 'finalize');
  }
</script>

<div class="mp-slot" class:mp-slot-filled={hasEntries} class:mp-slot-compact={compact}>
  {#if !hasEntries}
    <button type="button" class="mp-slot-empty" aria-label="Add a meal" onclick={onAdd}>
      <svg viewBox="0 0 24 24" width="28" height="28" aria-hidden="true">
        <path d="M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z" fill="currentColor" />
      </svg>
    </button>
  {/if}
  <!-- The dnd zone. Always rendered (an empty slot is a live drop target); when
       empty it overlays the ＋ button pointer-transparently — drop detection is
       coordinate-based, so clicks pass through while drops still land. Only item
       elements may live inside (svelte-dnd-action's contract). -->
  <div
    class="mp-slot-entries"
    class:mp-slot-entries-overlay={!hasEntries}
    use:dndzone={{
      items: entries,
      type: 'meal-plan-entries',
      flipDurationMs: 150,
      dropTargetStyle: {},
      dropTargetClasses: ['mp-drop-active'],
      delayTouchStart: 250,
    }}
    onconsider={handleConsider}
    onfinalize={handleFinalize}
  >
    {#each entries as entry (entry.id)}
        {#if entry.recipe}
          <div class="mp-entry">
            <span class="mp-entry-grip" aria-hidden="true">
              <svg viewBox="0 0 24 24" width="12" height="16">
                <path
                  d="M9 5a2 2 0 1 1-4 0 2 2 0 0 1 4 0zm0 7a2 2 0 1 1-4 0 2 2 0 0 1 4 0zm0 7a2 2 0 1 1-4 0 2 2 0 0 1 4 0zm10-14a2 2 0 1 1-4 0 2 2 0 0 1 4 0zm0 7a2 2 0 1 1-4 0 2 2 0 0 1 4 0zm0 7a2 2 0 1 1-4 0 2 2 0 0 1 4 0z"
                  fill="currentColor"
                />
              </svg>
            </span>
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
            <span class="mp-entry-grip" aria-hidden="true">
              <svg viewBox="0 0 24 24" width="12" height="16">
                <path
                  d="M9 5a2 2 0 1 1-4 0 2 2 0 0 1 4 0zm0 7a2 2 0 1 1-4 0 2 2 0 0 1 4 0zm0 7a2 2 0 1 1-4 0 2 2 0 0 1 4 0zm10-14a2 2 0 1 1-4 0 2 2 0 0 1 4 0zm0 7a2 2 0 1 1-4 0 2 2 0 0 1 4 0zm0 7a2 2 0 1 1-4 0 2 2 0 0 1 4 0z"
                  fill="currentColor"
                />
              </svg>
            </span>
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
  </div>
  {#if hasEntries}
    <button type="button" class="mp-slot-add-more" onclick={onAdd}>
      <svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true">
        <path d="M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z" fill="currentColor" />
      </svg>
      Add side/dessert
    </button>
  {/if}
</div>

<style>
  .mp-slot {
    position: relative;
    min-height: 80px;
    width: 100%;
    border-radius: var(--radius-sm);
  }

  .mp-slot-empty {
    display: grid;
    place-items: center;
    width: 100%;
    min-height: 80px;
    border: 2px dashed var(--color-line-strong);
    border-radius: var(--radius-sm);
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
    padding: 8px;
  }
  .mp-slot-entries {
    display: flex;
    flex-direction: column;
    gap: 4px;
    border-radius: var(--radius-sm);
  }
  /* An EMPTY slot's zone stretches over the ＋ button. Pointer-transparent so
     tap-to-add still works — svelte-dnd-action's drop detection is
     coordinate-based, not pointer-event-based, so drops still register. */
  .mp-slot-entries-overlay {
    position: absolute;
    inset: 0;
    z-index: 1;
    pointer-events: none;
    padding: 8px;
  }
  /* Hovered-drop affordance (dropTargetClasses) — on both empty and filled zones. */
  .mp-slot-entries:global(.mp-drop-active) {
    outline: 2px dashed var(--color-primary);
    outline-offset: 2px;
    background: var(--color-action-hover);
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

  /* Drag affordance. The WHOLE card drags (mouse: press-drag; touch: long-press
     ~250ms then drag) — the grip is the visual cue, kept always visible so
     phones get the affordance without hover. */
  .mp-entry-grip {
    display: grid;
    place-items: center;
    width: 14px;
    flex-shrink: 0;
    margin-left: 2px;
    color: var(--color-text-muted);
    opacity: 0.45;
    cursor: grab;
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
    line-height: 1.25;
    font-size: 0.875rem;
    display: -webkit-box;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 2;
    line-clamp: 2;
    overflow: hidden;
    overflow-wrap: anywhere;
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

  /* Overlaid on the card's top-right (absolute — it must not reserve card width;
     in a ~150px calendar cell every reserved px comes out of the recipe name). */
  .mp-entry-remove {
    position: absolute;
    top: 2px;
    right: 2px;
    z-index: 1;
    display: grid;
    place-items: center;
    width: 26px;
    height: 26px;
    border: none;
    background: var(--color-surface);
    box-shadow: var(--shadow-1);
    color: var(--color-text-muted);
    border-radius: 50%;
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

  /* ── Compact (calendar-grid) layout ─────────────────────────────────────
     Stacked card: image as a full-width banner, the name below at full cell
     width with up to 3 lines. Vertical space is cheap on the grid — rows
     grow; legibility wins. (See the `compact` prop doc for why this is a
     class variant and not a container query.) */
  .mp-slot-compact .mp-entry {
    display: block;
  }
  .mp-slot-compact .mp-entry-main {
    width: 100%;
    flex-direction: column;
    align-items: stretch;
    gap: 4px;
    padding: 4px 4px 6px;
  }
  .mp-slot-compact .mp-entry-image,
  .mp-slot-compact .mp-entry-placeholder,
  .mp-slot-compact .mp-entry-custom-icon {
    width: 100%;
    height: 44px;
    object-fit: cover;
  }
  .mp-slot-compact .mp-entry-name {
    font-size: 0.8125rem;
    -webkit-line-clamp: 3;
    line-clamp: 3;
  }
  .mp-slot-compact .mp-entry-notes {
    white-space: normal;
    display: -webkit-box;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 2;
    line-clamp: 2;
  }
  /* The grip rides the banner's top-left corner instead of owning a column. */
  .mp-slot-compact .mp-entry-grip {
    position: absolute;
    top: 4px;
    left: 4px;
    z-index: 1;
    width: 18px;
    height: 20px;
    margin-left: 0;
    border-radius: var(--radius-sm);
    background: color-mix(in srgb, var(--color-surface) 75%, transparent);
    opacity: 0.9;
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
