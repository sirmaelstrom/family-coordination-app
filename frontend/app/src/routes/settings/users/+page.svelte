<script lang="ts">
  // Settings → Family Members route (/settings/users → the settings island's
  // UsersApp, the old data-view="users"). Identity from $lib/session via ctx() (M8).
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import UsersApp from '$lib/settings/UsersApp.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <UsersApp ctx={ctx({ view: 'users' })} />
</IslandRoute>
