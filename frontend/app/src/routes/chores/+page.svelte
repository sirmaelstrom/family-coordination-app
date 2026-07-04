<script lang="ts">
  // Chores route. Identity comes from the canonical $lib/session store (booted
  // once in the shell +layout.svelte) — NOT a per-route /api/me fetch (M8). The
  // store handles the 401 → /account/login redirect and surfaces load errors
  // through the shell layout's banner. Chores needs no route params; ctx() with
  // no args supplies the household/user identity the island reads.
  import App from '$lib/chores/App.svelte';
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
