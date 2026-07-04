<script lang="ts">
  // Meal-plan route. Identity comes from the canonical $lib/session store
  // (booted once in the shell +layout.svelte) — NOT a per-route /api/me fetch
  // (M8). Week navigation is island-internal state driving a weekStart query on
  // /api/meal-plan/board, so there is no browser URL to make base-aware.
  import App from '$lib/meal-plan/App.svelte';
  import { session, ctx } from '$lib/session.svelte';
</script>

{#if session.ready}
  <App ctx={ctx()} />
{:else if session.status !== 'error'}
  <p class="route-status">Loading…</p>
{/if}

<style>
  .route-status {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted, #666);
  }
</style>
