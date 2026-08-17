<script lang="ts">
  // Past shopping lists route. Identity comes from the canonical $lib/session store
  // (booted once in the shell +layout.svelte) — NOT a per-route /api/me fetch (M8).
  // Static /past wins over the sibling [listId] param route by SvelteKit precedence,
  // and the existing /shopping-list fallback prefix in Program.cs covers this nested
  // path — no server change needed.
  import PastApp from '$lib/shopping-list/PastApp.svelte';
  import { session, ctx } from '$lib/session.svelte';
</script>

{#if session.ready}
  <PastApp ctx={ctx({ listId: null })} />
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
