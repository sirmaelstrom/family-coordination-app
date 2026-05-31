<script lang="ts">
  import type { ChoreLensId } from '../types';
  import { CHORE_LENSES } from '../types';

  // ───────────────────────────────────────────────────────────────────────
  // Lens switcher — segmented control over the canonical lens ids, plus the
  // roaming "set as default" control (WP-12).
  //
  // Switching a lens is CLIENT-SIDE only (it sets the store `lens`); it NEVER
  // refetches the board (M11). Every lens groups the ONE already-loaded payload.
  //
  // The 📌 control pins the ACTIVE lens as the user's per-user default. The
  // default is SERVER-PERSISTED (User.ChoresDefaultView) so it roams across
  // devices — NOT localStorage (D18). The island reads the current default from
  // the board payload's `userDefaultView` (handled in the store); this control
  // only writes it via `onSetDefault`.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    /** The active lens id (read from the store). */
    active: ChoreLensId;
    /** The user's persisted default lens (null ⇒ Needs-attention). */
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
    'needs-attention': 'Needs attention',
    rooms: 'Rooms',
    'up-for-grabs': 'Up for grabs',
    mine: 'Mine',
    equity: 'Equity',
  };

  let isActiveDefault = $derived(active === defaultLens);
</script>

<div class="ch-switcher-bar">
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
        {#if lens === defaultLens}
          <span class="ch-default-dot" title="Your default view" aria-label="Your default view">📌</span>
        {/if}
      </button>
    {/each}
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
    gap: 12px;
    flex-wrap: wrap;
    justify-content: space-between;
  }
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
  .ch-default-dot {
    font-size: 0.6875rem;
    line-height: 1;
  }

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
