<script lang="ts">
  // Import route (/recipes/import). The island's ImportDialog is a modal opened from
  // within ListApp — there is no standalone import view — so this route renders
  // ListApp with the dialog auto-opened. Closing the dialog returns to /recipes
  // (base-aware); a successful import follows the island's own base-aware nav to the
  // new/edited recipe. Identity from $lib/session via ctx() (M8).
  import { base } from '$app/paths';
  import { goto } from '$app/navigation';
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import ListApp from '$lib/recipes/ListApp.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <ListApp openImport onImportClose={() => goto(`${base}/recipes`)} ctx={ctx({ view: 'list' })} />
</IslandRoute>
