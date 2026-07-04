<script lang="ts">
  // Recipe edit route (/recipes/edit/{id} → EditApp, the island's data-view="edit").
  // recipeId comes from the route param (kept in the /recipes/edit/{id} shape the
  // island already navigates to — do NOT reshape). Identity from $lib/session (M8).
  import { page } from '$app/state';
  import EditApp from '$lib/recipes/EditApp.svelte';
  import { session } from '$lib/session.svelte';

  const recipeId = $derived.by(() => {
    const n = Number(page.params.id);
    return Number.isFinite(n) ? n : null;
  });
</script>

{#if session.ready}
  <EditApp
    ctx={{
      householdId: session.householdId!,
      userId: session.userId!,
      userName: session.userName!,
      view: 'edit',
      recipeId,
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
