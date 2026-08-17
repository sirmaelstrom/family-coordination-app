<script lang="ts">
  // The island route shell (quest 76e6f169): every SPA route gates its island on
  // session.ready and shows the same loading state — this component is that gate,
  // once, instead of 15 copies. Identity still comes from the canonical
  // $lib/session store booted in +layout.svelte (M8); the store handles the
  // 401 → /account/login redirect and surfaces load errors through the shell
  // layout's banner, which is why the error branch here renders nothing.
  import type { Snippet } from 'svelte';
  import { session } from '$lib/session.svelte';

  let { children }: { children: Snippet } = $props();
</script>

{#if session.ready}
  {@render children()}
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
