<script lang="ts">
  // Settings → Household Requests route (/settings/households → the admin island's
  // HouseholdsApp). SITE-ADMIN ONLY: the nav entry is gated on session.isSiteAdmin
  // (shell Nav), and a non-admin who reaches the route gets the API's 403 surfaced
  // as the island's built-in "Access denied" panel (R-C4) inside the shell — never
  // a blank page. Identity from $lib/session via ctx() (M8).
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import HouseholdsApp from '$lib/admin/HouseholdsApp.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <HouseholdsApp ctx={ctx({ view: 'households' })} />
</IslandRoute>
