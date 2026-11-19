<script lang="ts">
  // Settings → Deleted Recipes route (/settings/deleted-recipes → the settings
  // island's DeletedRecipesApp). Identity from #lib/session via ctx() (M8); the
  // view discriminator rides the typed ctx({view}) overload.
  import IslandRoute from '#lib/shared/IslandRoute.svelte';
  import DeletedRecipesApp from '#lib/settings/DeletedRecipesApp.svelte';
  import { ctx } from '#lib/session.svelte.ts';
</script>

<IslandRoute>
  <DeletedRecipesApp ctx={ctx({ view: 'deleted-recipes' })} />
</IslandRoute>
