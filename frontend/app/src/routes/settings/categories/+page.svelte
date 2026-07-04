<script lang="ts">
  // Settings → Categories route (/settings/categories → the settings island's
  // CategoriesApp, the old data-view="categories"). Identity from the canonical
  // $lib/session store (M8). The settings island's ShellContext carries a `view`
  // discriminator, so the route builds the ctx literal from session identity.
  import CategoriesApp from '$lib/settings/CategoriesApp.svelte';
  import { session } from '$lib/session.svelte';
</script>

{#if session.ready}
  <CategoriesApp
    ctx={{
      householdId: session.householdId!,
      userId: session.userId!,
      userName: session.userName!,
      view: 'categories',
    }}
  />
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
