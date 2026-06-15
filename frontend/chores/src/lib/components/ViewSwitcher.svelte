<script lang="ts">
  import type { ChoreLensId } from '../types';

  // ───────────────────────────────────────────────────────────────────────
  // View control (Phase 14 — Model A board IA). Two grouped controls + the
  // roaming 📌 "set as default":
  //
  //   PRIMARY (prominent segmented control) — the board's main affordance. It
  //   picks WHICH chore set the always-attention-sectioned board shows:
  //     Up for grabs · Mine · All   (lens ids up-for-grabs / mine / needs-attention)
  //
  //   SECONDARY (visually lighter, off to the side) — on-demand ORGANIZERS that
  //   reorganize the board differently, NOT co-equal filters:
  //     Rooms · Equity   (Equity reads as the most tucked-away — back seat)
  //
  // Switching is CLIENT-SIDE only (sets the store `lens`); it NEVER refetches
  // (M11). Every view groups the ONE already-loaded payload (Equity keeps its
  // own separate cached fetch).
  //
  // The 📌 control pins the ACTIVE view (any of the 5 ids) as the user's per-user
  // default. The default is SERVER-PERSISTED (User.ChoresDefaultView) so it roams
  // across devices — NOT localStorage (D18). The island reads the current default
  // from the board payload's `userDefaultView` (in the store); this control only
  // writes it via `onSetDefault`, and shows the 📌 dot on whichever view is the
  // current default (across BOTH groups).
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    /** The active lens id (read from the store). */
    active: ChoreLensId;
    /** The user's persisted default lens (null ⇒ Up-for-grabs, resolved upstream). */
    defaultLens: ChoreLensId;
    /** True while a default-view PATCH is in flight (disables the control). */
    saving: boolean;
    /** Set the active lens (writes the store). Switching never refetches (M11). */
    onSelect: (lens: ChoreLensId) => void;
    /** Pin the active lens as the roaming default (PATCH /me/default-view). */
    onSetDefault: (lens: ChoreLensId) => void;
  }

  let { active, defaultLens, saving, onSelect, onSetDefault }: Props = $props();

  const LENS_LABEL: Record<ChoreLensId, string> = {
    'up-for-grabs': 'Up for grabs',
    mine: 'Mine',
    'needs-attention': 'All',
    rooms: 'Rooms',
    equity: 'Board load',
  };

  // The primary segmented filters (board filters) vs the secondary organizers.
  const PRIMARY: ChoreLensId[] = ['up-for-grabs', 'mine', 'needs-attention'];
  const SECONDARY: ChoreLensId[] = ['rooms', 'equity'];

  let isActiveDefault = $derived(active === defaultLens);
</script>

<div class="ch-switcher-bar">
  <div class="ch-switcher-views">
    <!-- PRIMARY: the prominent segmented filter — the main affordance. -->
    <div class="ch-segment" role="tablist" aria-label="Board filter">
      {#each PRIMARY as lens (lens)}
        <button
          type="button"
          role="tab"
          class="ch-segment-tab"
          class:active={lens === active}
          aria-selected={lens === active}
          onclick={() => onSelect(lens)}
        >
          {LENS_LABEL[lens]}
          {#if lens === defaultLens}
            <span class="ch-default-dot" title="Your default view" aria-label="Your default view">📌</span>
          {/if}
        </button>
      {/each}
    </div>

    <!-- SECONDARY: lighter, de-emphasized organizers (Equity tucked furthest). -->
    <div class="ch-organizers" role="group" aria-label="Other views">
      {#each SECONDARY as lens (lens)}
        <button
          type="button"
          class="ch-organizer"
          class:active={lens === active}
          class:is-equity={lens === 'equity'}
          aria-pressed={lens === active}
          onclick={() => onSelect(lens)}
        >
          {LENS_LABEL[lens]}
          {#if lens === defaultLens}
            <span class="ch-default-dot" title="Your default view" aria-label="Your default view">📌</span>
          {/if}
        </button>
      {/each}
    </div>
  </div>

  <button
    type="button"
    class="ch-set-default"
    class:is-default={isActiveDefault}
    onclick={() => onSetDefault(active)}
    disabled={saving || isActiveDefault}
    aria-pressed={isActiveDefault}
    title={isActiveDefault
      ? `“${LENS_LABEL[active]}” is your default view`
      : `Open onto “${LENS_LABEL[active]}” by default`}
  >
    <span class="ch-set-default-icon" aria-hidden="true">📌</span>
    {#if isActiveDefault}
      Default view
    {:else if saving}
      Saving…
    {:else}
      Set as default
    {/if}
  </button>
</div>

<style>
  .ch-switcher-bar {
    display: flex;
    align-items: center;
    gap: 16px;
    flex-wrap: wrap;
    justify-content: space-between;
  }
  .ch-switcher-views {
    display: flex;
    align-items: center;
    gap: 16px;
    flex-wrap: wrap;
    min-width: 0;
  }

  /* ── PRIMARY segmented control (prominent) ──────────────────────────────── */
  .ch-segment {
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
  .ch-segment::-webkit-scrollbar {
    display: none;
  }
  .ch-segment-tab {
    flex: 0 0 auto;
    display: inline-flex;
    align-items: center;
    gap: 5px;
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
  .ch-segment-tab:hover:not(.active) {
    color: var(--color-text);
  }
  .ch-segment-tab.active {
    background: var(--color-surface);
    color: var(--color-text);
    box-shadow: var(--shadow-1);
  }
  .ch-segment-tab:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: -2px;
  }

  /* ── SECONDARY organizers (de-emphasized) ───────────────────────────────── */
  .ch-organizers {
    display: inline-flex;
    align-items: center;
    gap: 4px;
  }
  .ch-organizer {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    padding: 6px 10px;
    min-height: 34px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    white-space: nowrap;
    opacity: 0.8;
    transition:
      background-color 0.15s,
      color 0.15s,
      opacity 0.15s;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-organizer:hover:not(.active) {
    color: var(--color-text);
    background: var(--color-action-hover);
    opacity: 1;
  }
  .ch-organizer.active {
    color: var(--color-text);
    background: var(--color-action-hover);
    opacity: 1;
    box-shadow: inset 0 -2px 0 var(--color-primary);
  }
  .ch-organizer:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: -2px;
  }
  /* Equity reads as the most tucked-away (back seat) — lighter still. */
  .ch-organizer.is-equity:not(.active) {
    font-size: 0.75rem;
    opacity: 0.65;
  }

  .ch-default-dot {
    font-size: 0.6875rem;
    line-height: 1;
  }

  /* ── 📌 roaming set-as-default ───────────────────────────────────────────── */
  .ch-set-default {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    color: var(--color-text-muted);
    padding: 7px 14px;
    min-height: 38px;
    border-radius: 19px;
    cursor: pointer;
    white-space: nowrap;
    transition:
      background-color 0.15s,
      color 0.15s,
      border-color 0.15s;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-set-default:hover:not(:disabled) {
    color: var(--color-text);
    background: var(--color-action-hover);
  }
  .ch-set-default:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: 2px;
  }
  .ch-set-default.is-default {
    color: var(--color-primary);
    border-color: var(--color-primary);
    cursor: default;
  }
  .ch-set-default:disabled:not(.is-default) {
    opacity: 0.55;
    cursor: not-allowed;
  }
  .ch-set-default-icon {
    font-size: 0.875rem;
    line-height: 1;
  }
</style>
