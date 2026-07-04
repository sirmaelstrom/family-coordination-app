<script lang="ts">
  // Deep-linked shopping-list route: /app/shopping-list/{listId}. Renders the
  // same island as the base route; the only difference is the pre-selected
  // list, taken from the route param so a hard refresh on a deep link lands on
  // the right list. Identity still comes from $lib/session (M8) — no per-route
  // /api/me fetch. Once mounted, in-app list switches are a shallow
  // history.replaceState (App.svelte:syncUrl), so this route does not remount.
  import { page } from '$app/state';
  import App from '$lib/shopping-list/App.svelte';
  import { session, ctx } from '$lib/session.svelte';

  const listId = $derived.by(() => {
    const n = Number(page.params.listId);
    return Number.isFinite(n) ? n : null;
  });
</script>

{#if session.ready}
  <App ctx={ctx({ listId })} />
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
