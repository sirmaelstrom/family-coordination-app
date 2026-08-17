<script lang="ts">
  // Dashboard route (/dashboard → the read-only dashboard island over GET /api/dashboard).
  // Also the SPA's landing surface: /app redirects here (see routes/+page.svelte).
  // Identity comes from the canonical $lib/session store via ctx() (M8); the
  // session.ready gate + loading state live in IslandRoute.
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import App from '$lib/dashboard/App.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <App ctx={ctx()} />
</IslandRoute>
