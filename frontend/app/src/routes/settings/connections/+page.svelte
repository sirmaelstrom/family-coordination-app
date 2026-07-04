<script lang="ts">
  // Settings → Connections route (/settings/connections → the connections island's
  // invite-code flow). Identity from $lib/session (M8); connections' ShellContext
  // is the plain {householdId,userId,userName} shape, so ctx() supplies it drop-in.
  import App from '$lib/connections/App.svelte';
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
