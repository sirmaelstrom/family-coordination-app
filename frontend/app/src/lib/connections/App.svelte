<script lang="ts">
  // Root of the connections island (#connections-root). Reads the ShellContext from
  // the root data-attrs (passed by main.ts), loads the active invite + connected
  // list ONCE, then drives the invite state machine via the shared store. No writes
  // happen on load; no liveness/poll (parity — Connections.razor doesn't poll).
  import { untrack } from 'svelte';
  import './styles/app.css';
  import type { ShellContext } from './lib/types';
  import { connectionsStore as store } from './lib/connectionsStore.svelte';
  import InviteShare from './lib/components/InviteShare.svelte';
  import CodeEntry from './lib/components/CodeEntry.svelte';
  import ConnectedList from './lib/components/ConnectedList.svelte';
  import DisconnectDialog from './lib/components/DisconnectDialog.svelte';

  let { ctx }: { ctx: ShellContext } = $props();

  async function load(): Promise<void> {
    await store.load();
  }

  // ⚠ Loop safety (memory svelte5-setup-effect-async-loader-loop): the one-time
  // setup effect calls load(), whose sync prefix reads THEN writes store state.
  // Wrapping the whole body in untrack() gives the effect zero reactive deps so it
  // runs exactly once. There is no liveness here (parity — the page doesn't poll).
  $effect(() => {
    untrack(() => {
      void ctx; // shell context available; the store calls /api directly (household resolved server-side)
      void load();
    });
  });
</script>

<div class="con-page">
  <h1 class="con-title">Family Connections</h1>

  {#if !store.loaded && store.loading}
    <div class="con-loading">Loading…</div>
  {:else if !store.loaded && store.error}
    <div class="con-inline-error" role="alert">
      <span>{store.error}</span>
      <button type="button" class="con-retry" onclick={() => void load()}>Retry</button>
    </div>
  {:else}
    <InviteShare />
    <CodeEntry />
    <ConnectedList />
  {/if}
</div>

<DisconnectDialog />
