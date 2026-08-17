<script lang="ts">
  // Past shopping lists route. Static /past wins over the sibling [listId] param
  // route by SvelteKit precedence, and the existing /shopping-list fallback prefix
  // in Program.cs covers this nested path — no server change needed. Identity from
  // $lib/session via ctx() (M8).
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import PastApp from '$lib/shopping-list/PastApp.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <PastApp ctx={ctx({ listId: null })} />
</IslandRoute>
