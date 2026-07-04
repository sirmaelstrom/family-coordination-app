<script lang="ts">
  // Share section (parity Connections.razor:32-82): generate a code, show it with
  // expiry + copy + cancel, or the "Create Invite Code" button when there's none.
  import { connectionsStore as store } from '../connectionsStore.svelte';
  import { formatExpiry } from '../dates';
</script>

<section class="con-card">
  <h2 class="con-card-title">Share Your Recipes</h2>
  <p class="con-card-sub">
    Share this code with your family or friends — they'll enter it in their app to start sharing recipes together.
  </p>

  {#if store.generating}
    <div class="con-share"><span class="con-spinner" aria-label="Creating invite"></span></div>
  {:else if store.activeInvite}
    <div class="con-share">
      <div class="con-code">{store.activeInvite.code}</div>
      <button type="button" class="con-btn-outline" onclick={() => store.copyCode()}>📋 Copy Code</button>
      <div class="con-expiry">Expires {formatExpiry(store.activeInvite.expiresAt)}</div>
      <button type="button" class="con-btn-danger" onclick={() => store.cancelActiveInvite()}>Cancel Invite</button>
    </div>
  {:else}
    <button type="button" class="con-btn-primary" onclick={() => store.generate()}>Create Invite Code</button>
  {/if}
</section>
