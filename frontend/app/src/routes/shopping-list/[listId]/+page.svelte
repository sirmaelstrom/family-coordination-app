<script lang="ts">
  // Deep-linked shopping-list route: /app/shopping-list/{listId}. Same island as
  // the base route with the pre-selected list from the route param, so a hard
  // refresh on a deep link lands on the right list. In-app list switches are a
  // shallow history.replaceState (App.svelte:syncUrl), so this route does not
  // remount. Identity from $lib/session via ctx() (M8).
  import { page } from '$app/state';
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import App from '$lib/shopping-list/App.svelte';
  import { ctx } from '$lib/session.svelte';

  const listId = $derived.by(() => {
    const n = Number(page.params.listId);
    return Number.isFinite(n) ? n : null;
  });
</script>

<IslandRoute>
  <App ctx={ctx({ listId })} />
</IslandRoute>
