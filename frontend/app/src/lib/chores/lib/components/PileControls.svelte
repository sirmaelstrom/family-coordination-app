<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Up-for-grabs pile controls (Phase 15 R4′ — the "capacity-fit" affordances).
  // Rendered ONLY on the up-for-grabs lens (App.svelte gates it) — pull-only:
  // every pixel here exists because the viewer opened that lens (M3).
  //
  // Two controls, both up-for-grabs-only + pull-only:
  //   "Quick first" (WP-01) — a symmetric PRESENTATION sort (effort-ascending). It
  //   reads only the chore's declared weight; identical for every member (M6).
  //   "Fits me" (WP-03) — a SELF-ONLY filter to the tiers that fit the viewer's own
  //   capacity. It renders ONLY for a Reduced/Minimal caller (the store owns the
  //   whitelist gate via `showFitsMe`); a Full/unset viewer never sees it. The
  //   filter's subject is the chore's weight × the viewer's OWN tier — never a
  //   per-person share/history (M1/M6/MN1).
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    /** Is "Quick first" ordering on? (store.pileQuickFirst) */
    quickFirst: boolean;
    /** Toggle "Quick first" — the store flips its session-only field. */
    onToggleQuickFirst: () => void;
    /** Is "Fits me" filtering on? (store.pileFitsMe) */
    fitsMe: boolean;
    /** Render the "Fits me" chip at all? WHITELIST gate — Reduced/Minimal callers only (store.showFitsMeChip). */
    showFitsMe: boolean;
    /** Toggle "Fits me" — the store flips its session-only field. */
    onToggleFitsMe: () => void;
  }

  let { quickFirst, onToggleQuickFirst, fitsMe, showFitsMe, onToggleFitsMe }: Props = $props();
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
  {#if showFitsMe}
    <!--
      Self-only "Fits me" filter. Shown ONLY for a Reduced/Minimal caller — a
      Full/unset viewer never renders this chip, so the pile stays identical for
      them (the founding-case guarantee). It matches the chore's weight to the
      viewer's OWN capacity setting; never to anyone's share or history.
    -->
    <button
      type="button"
      class="ch-pile-chip"
      aria-pressed={fitsMe}
      onclick={onToggleFitsMe}
      title="Show chores that fit your capacity setting"
    >
      Fits me
    </button>
  {/if}
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
