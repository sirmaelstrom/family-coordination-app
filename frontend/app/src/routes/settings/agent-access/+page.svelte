<script lang="ts">
  // Settings → Agent access (/settings/agent-access, quest 8bfa42cf). Identity from $lib/session via ctx() (M8);
  // the server's /settings/{**slug} fallback already serves index.html for it.
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import App from '$lib/agent-access/App.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <App ctx={ctx()} />
</IslandRoute>
