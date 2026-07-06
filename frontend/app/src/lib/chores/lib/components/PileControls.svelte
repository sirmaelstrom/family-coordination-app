<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Up-for-grabs pile controls (Phase 15 R4′ — the "capacity-fit" affordances).
  // Rendered ONLY on the up-for-grabs lens (App.svelte gates it) — pull-only:
  // every pixel here exists because the viewer opened that lens (M3).
  //
  // WP-01 ships ONE control: "Quick first", a symmetric PRESENTATION toggle that
  // orders the pile effort-ascending (the store owns the sort). It reads only the
  // chore's declared weight — nothing personal, identical for every member (M6).
  //
  // WP-03 adds the self-only "Fits me" chip + set-aside row + provenance note
  // beside it; this component is the seam that hosts them.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    /** Is "Quick first" ordering on? (store.pileQuickFirst) */
    quickFirst: boolean;
    /** Toggle "Quick first" — the store flips its session-only field. */
    onToggleQuickFirst: () => void;
  }

  let { quickFirst, onToggleQuickFirst }: Props = $props();
</script>

<div class="ch-pile-controls" role="group" aria-label="Pile controls">
  <button
    type="button"
    class="ch-pile-chip"
    aria-pressed={quickFirst}
    onclick={onToggleQuickFirst}
    title="Show quicker chores first"
  >
    Quick first
  </button>
</div>

<style>
  .ch-pile-controls {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 8px;
    margin-bottom: 16px;
  }

  /* Pill toggle — mirrors the shipped chip idiom (set-as-default / meta tags):
     bordered, rounded, muted until pressed; pressed borrows the primary accent. */
  .ch-pile-chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    color: var(--color-text-muted);
    background: var(--color-surface);
    border: 1px solid var(--color-line-strong);
    border-radius: 999px;
    padding: 6px 14px;
    min-height: 34px;
    cursor: pointer;
    white-space: nowrap;
    transition:
      color 0.15s,
      border-color 0.15s,
      background-color 0.15s;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-pile-chip:hover {
    color: var(--color-text);
    background: var(--color-action-hover);
  }
  .ch-pile-chip[aria-pressed='true'] {
    color: var(--color-primary);
    border-color: var(--color-primary);
    background: color-mix(in srgb, var(--color-primary) 8%, var(--color-surface));
  }
  .ch-pile-chip:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: 2px;
  }
</style>
