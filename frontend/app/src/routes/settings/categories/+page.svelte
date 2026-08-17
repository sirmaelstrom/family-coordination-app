<script lang="ts">
  // Settings → Categories route (/settings/categories → the settings island's
  // CategoriesApp, the old data-view="categories"). Identity from $lib/session
  // via ctx() (M8); the view discriminator rides the typed ctx({view}) overload.
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import CategoriesApp from '$lib/settings/CategoriesApp.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <CategoriesApp ctx={ctx({ view: 'categories' })} />
</IslandRoute>
