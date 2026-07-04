<script lang="ts">
  // Recipes list route (/recipes → ListApp, the island's old data-view="list").
  // Identity comes from the canonical $lib/session store (M8) — NOT a per-route
  // /api/me fetch. The recipes island's ShellContext carries a `view`
  // discriminator (list|edit) + recipeId, so the route builds the ctx literal
  // directly from session identity rather than the shared ctx() helper.
  import ListApp from '$lib/recipes/ListApp.svelte';
  import { session } from '$lib/session.svelte';
</script>

{#if session.ready}
  <ListApp
    ctx={{
      householdId: session.householdId!,
      userId: session.userId!,
      userName: session.userName!,
      view: 'list',
      recipeId: null,
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
