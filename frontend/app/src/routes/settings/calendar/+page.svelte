<script lang="ts">
  // Settings → Calendar Feed route (/settings/calendar → the calendar-feed island,
  // quest 17e38a88). Identity from $lib/session via ctx() (M8); the server's
  // /settings/{**slug} fallback already serves index.html for it.
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import App from '$lib/calendar-feed/App.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <App ctx={ctx()} />
</IslandRoute>
