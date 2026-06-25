<script lang="ts">
  // Connected-families section (parity Connections.razor:153-188): list each
  // connected household with its connected-date + a Stop Sharing button (opens the
  // disconnect-confirm dialog), or an empty hint.
  import { connectionsStore as store } from '../connectionsStore.svelte';
  import { formatConnectedDate } from '../dates';
</script>

<section class="con-card">
  <h2 class="con-card-title">Your Connected Families</h2>

  {#if store.connected.length === 0}
    <p class="con-empty">
      You haven't connected with any families yet. Create an invite code or enter one to get started.
    </p>
  {:else}
    <ul class="con-list">
      {#each store.connected as family (family.householdId)}
        <li class="con-row">
          <div>
            <div class="con-row-name">{family.householdName}</div>
            <div class="con-row-date">Connected {formatConnectedDate(family.connectedAt)}</div>
          </div>
          <button type="button" class="con-btn-danger" onclick={() => store.askDisconnect(family)}>
            🔗 Stop Sharing
          </button>
        </li>
      {/each}
    </ul>
  {/if}
</section>
