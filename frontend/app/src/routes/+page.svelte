<script lang="ts">
  // SPA index (/app). The app has no content of its own at the root, so it
  // redirects to the dashboard. The redirect is a client-side goto in onMount —
  // NOT a +page.ts SSR `redirect` — because the app runs ssr=false (+layout.ts).
  // replaceState keeps the redirecting index out of the history stack. A minimal
  // loading body (not an empty page) avoids a blank-white flash during the
  // one-tick redirect. [R2 — closes the WP-08 Minor]
  import { onMount } from 'svelte';
  import { base } from '$app/paths';
  import { goto } from '$app/navigation';

  onMount(() => {
    void goto(`${base}/dashboard`, { replaceState: true });
  });
</script>

<div class="index-loading">
  <p>Loading…</p>
</div>

<style>
  .index-loading {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted, #666);
  }
</style>
