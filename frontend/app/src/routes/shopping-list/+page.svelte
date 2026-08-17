<script lang="ts">
  // Shopping-list base route: pre-selects no list; App.svelte auto-picks the
  // first/favorite. Identity from $lib/session via ctx() (M8).
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import App from '$lib/shopping-list/App.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <App ctx={ctx({ listId: null })} />
</IslandRoute>
