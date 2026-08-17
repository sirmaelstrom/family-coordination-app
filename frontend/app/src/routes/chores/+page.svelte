<script lang="ts">
  // Chores route. Identity from the canonical $lib/session store via ctx() (M8);
  // the store handles 401 → /account/login and surfaces load errors through the
  // shell layout's banner. Chores needs no route params.
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import App from '$lib/chores/App.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <App ctx={ctx()} />
</IslandRoute>
