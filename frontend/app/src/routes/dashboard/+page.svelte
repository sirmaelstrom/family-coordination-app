<script lang="ts">
  // Dashboard route (/dashboard → the read-only dashboard island over
  // GET /api/dashboard). Identity comes from the canonical $lib/session store
  // (M8) — no per-route /api/me fetch. This is also the SPA's landing surface:
  // /app redirects here (see routes/+page.svelte).
  import App from '$lib/dashboard/App.svelte';
  import { session, ctx } from '$lib/session.svelte';
</script>

{#if session.ready}
  <App ctx={ctx()} />
{:else if session.status !== 'error'}
  <p class="route-status">Loading…</p>
{/if}

<style>
  .route-status {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted, #666);
  }
</style>
