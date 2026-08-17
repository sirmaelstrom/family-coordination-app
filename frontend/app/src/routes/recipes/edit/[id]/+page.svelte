<script lang="ts">
  // Recipe edit route (/recipes/edit/{id} → EditApp, the island's data-view="edit").
  // recipeId comes from the route param (kept in the /recipes/edit/{id} shape the
  // island already navigates to — do NOT reshape). Identity from $lib/session (M8).
  import { page } from '$app/state';
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import EditApp from '$lib/recipes/EditApp.svelte';
  import { ctx } from '$lib/session.svelte';

  const recipeId = $derived.by(() => {
    const n = Number(page.params.id);
    return Number.isFinite(n) ? n : null;
  });
</script>

<IslandRoute>
  <EditApp ctx={ctx({ view: 'edit', recipeId })} />
</IslandRoute>
