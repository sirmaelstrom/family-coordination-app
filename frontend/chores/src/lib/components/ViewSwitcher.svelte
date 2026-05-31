<script lang="ts">
  import type { ChoreLensId } from '../types';
  import { CHORE_LENSES } from '../types';

  // ───────────────────────────────────────────────────────────────────────
  // Lens switcher — segmented control. This is CHROME only: it sets the
  // active lens in the store. The actual Rooms / Up-for-grabs / Mine grouping
  // UIs are WP-12; here only the Needs-attention board renders, but the
  // switcher + the store's `lens` state are the seam WP-12 extends.
  //
  // Persistence of the selection is in-session only (store state). WP-12 adds
  // the roaming default-view (`setDefaultView` + `board.userDefaultView`).
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    /** The active lens id (read from the store). */
    active: ChoreLensId;
    /** Set the active lens (writes the store). Switching never refetches (M11). */
    onSelect: (lens: ChoreLensId) => void;
  }

  let { active, onSelect }: Props = $props();

  const LENS_LABEL: Record<ChoreLensId, string> = {
    'needs-attention': 'Needs attention',
    rooms: 'Rooms',
    'up-for-grabs': 'Up for grabs',
    mine: 'Mine',
  };
</script>

<div class="ch-switcher" role="tablist" aria-label="Chore views">
  {#each CHORE_LENSES as lens (lens)}
    <button
      type="button"
      role="tab"
      class="ch-switch-tab"
      class:active={lens === active}
      aria-selected={lens === active}
      onclick={() => onSelect(lens)}
    >
      {LENS_LABEL[lens]}
    </button>
  {/each}
</div>

<style>
  .ch-switcher {
    display: inline-flex;
    gap: 2px;
    padding: 3px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
    max-width: 100%;
    overflow-x: auto;
    -webkit-overflow-scrolling: touch;
    scrollbar-width: none;
  }
  .ch-switcher::-webkit-scrollbar {
    display: none;
  }
  .ch-switch-tab {
    flex: 0 0 auto;
    font: inherit;
    font-size: 0.875rem;
    font-weight: 500;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    padding: 8px 16px;
    min-height: 38px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    white-space: nowrap;
    transition:
      background-color 0.15s,
      color 0.15s;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-switch-tab:hover:not(.active) {
    color: var(--color-text);
  }
  .ch-switch-tab.active {
    background: var(--color-surface);
    color: var(--color-text);
    box-shadow: var(--shadow-1);
  }
  .ch-switch-tab:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: -2px;
  }
</style>
