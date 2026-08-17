<script lang="ts">
  // New-recipe route (/recipes/new → EditApp with a null recipeId, the island's
  // data-view="edit" + no recipe-id). Identity from $lib/session via ctx() (M8).
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import EditApp from '$lib/recipes/EditApp.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <EditApp ctx={ctx({ view: 'edit' })} />
</IslandRoute>
