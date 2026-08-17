<script lang="ts">
  // Recipes list route (/recipes → ListApp, the island's data-view="list").
  // Identity from $lib/session via ctx() (M8) — the view discriminator rides
  // the typed ctx({view}) overload.
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import ListApp from '$lib/recipes/ListApp.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <ListApp ctx={ctx({ view: 'list' })} />
</IslandRoute>
