<script lang="ts">
  // Meal-plan route. Week navigation is island-internal state driving a weekStart
  // query on /api/meal-plan/board, so there is no browser URL to make base-aware.
  // Identity from $lib/session via ctx() (M8).
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import App from '$lib/meal-plan/App.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <App ctx={ctx()} />
</IslandRoute>
