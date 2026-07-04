<script lang="ts">
  // Shopping-list route. Identity comes from the canonical $lib/session store
  // (booted once in the shell +layout.svelte) — NOT a per-route /api/me fetch
  // (orchestrator M8). The store handles the 401 → /account/login redirect and
  // surfaces load errors through the shell layout's banner. This base route
  // pre-selects no list; App.svelte auto-picks the first/favorite.
  import App from '$lib/shopping-list/App.svelte';
  import { session, ctx } from '$lib/session.svelte';
</script>

{#if session.ready}
  <App ctx={ctx({ listId: null })} />
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
