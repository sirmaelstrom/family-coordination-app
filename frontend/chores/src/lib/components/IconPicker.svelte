<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // IconPicker (v1.2) — a tiny curated emoji palette, reused by the inline
  // "new room" form (room icon) and the chore add/edit sheets (chore icon).
  // Emoji match the island's existing visual language (room rollups already
  // render emoji icons). Pure presentational: the parent owns `value` and is
  // notified via `onSelect` — no internal state, no date math.
  // ───────────────────────────────────────────────────────────────────────
  interface Props {
    /** Currently-selected icon (emoji), or null for none. */
    value: string | null;
    /** Notified with the picked emoji. */
    onSelect: (icon: string) => void;
    /** Optional preset palette override. */
    icons?: string[];
    /** Accessible group label. */
    label?: string;
  }

  const DEFAULT_ICONS = [
    '🧹', '🍳', '🛏️', '🚿', '🛋️', '🧺', '🚪', '🌿',
    '🗑️', '🧽', '🐾', '🚗', '🪴', '🧸', '📦', '🔧',
  ];

  let { value, onSelect, icons = DEFAULT_ICONS, label = 'Icon' }: Props = $props();
</script>

<div class="ch-iconpick" role="group" aria-label={label}>
  {#each icons as icon (icon)}
    <button
      type="button"
      class="ch-iconpick-opt"
      class:active={value === icon}
      aria-pressed={value === icon}
      aria-label={icon}
      onclick={() => onSelect(icon)}
    >
      {icon}
    </button>
  {/each}
</div>

<style>
  .ch-iconpick {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }
  .ch-iconpick-opt {
    font: inherit;
    font-size: 1.125rem;
    line-height: 1;
    width: 40px;
    height: 40px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    border-radius: var(--radius-sm);
    cursor: pointer;
    transition:
      border-color 0.15s,
      background-color 0.15s,
      transform 0.1s;
    -webkit-tap-highlight-color: transparent;
  }
  .ch-iconpick-opt:hover:not(.active) {
    background: var(--color-action-hover);
  }
  .ch-iconpick-opt.active {
    border-color: var(--color-primary);
    background: var(--color-action-hover);
    box-shadow: inset 0 0 0 1px var(--color-primary);
  }
  .ch-iconpick-opt:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: 1px;
  }
</style>
