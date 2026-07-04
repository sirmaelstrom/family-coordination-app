<script lang="ts">
  // New-recipe route (/recipes/new → EditApp with a null recipeId, the island's
  // data-view="edit" + no recipe-id). Identity from $lib/session (M8).
  import EditApp from '$lib/recipes/EditApp.svelte';
  import { session } from '$lib/session.svelte';
</script>

{#if session.ready}
  <EditApp
    ctx={{
      householdId: session.householdId!,
      userId: session.userId!,
      userName: session.userName!,
      view: 'edit',
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
