<script lang="ts">
  // Import route (/recipes/import). The island's ImportDialog is a modal opened
  // from within ListApp — there is no standalone import view — so this route
  // renders ListApp with the dialog auto-opened on mount. Closing the dialog
  // returns to /recipes (base-aware); a successful import follows the island's
  // own base-aware nav to the new/edited recipe. Identity from $lib/session (M8).
  import { base } from '$app/paths';
  import { goto } from '$app/navigation';
  import ListApp from '$lib/recipes/ListApp.svelte';
  import { session } from '$lib/session.svelte';
</script>

{#if session.ready}
  <ListApp
    openImport
    onImportClose={() => goto(`${base}/recipes`)}
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
