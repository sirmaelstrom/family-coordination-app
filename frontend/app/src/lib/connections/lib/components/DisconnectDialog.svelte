<script lang="ts">
  // Disconnect-confirm dialog (parity Connections.razor:192-220): the household name
  // + 3 consequence bullets + a Stop Sharing (danger) action with a busy spinner.
  // Driven by store.disconnectTarget (a native <dialog> showModal/close).
  import { connectionsStore as store } from '../connectionsStore.svelte';

  let dialogEl: HTMLDialogElement | null = $state(null);
  const open = $derived(store.disconnectTarget !== null);

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      dialogEl.showModal();
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });
</script>

<dialog bind:this={dialogEl} onclose={() => store.cancelDisconnect()} class="con-dialog">
  {#if store.disconnectTarget}
    <div class="con-dialog-body">
      <h2 class="con-dialog-title">Stop sharing with {store.disconnectTarget.householdName}?</h2>
      <ul class="con-dialog-list">
        <li><span class="con-mark con-mark-no">✕</span> You'll no longer be able to browse their recipes</li>
        <li><span class="con-mark con-mark-no">✕</span> They'll no longer be able to browse yours</li>
        <li><span class="con-mark con-mark-yes">✓</span> Recipes you've already copied will stay in your collection</li>
      </ul>
      <div class="con-actions con-dialog-actions">
        <button type="button" class="con-btn-outline" onclick={() => store.cancelDisconnect()} disabled={store.disconnecting}>
          Cancel
        </button>
        <button type="button" class="con-btn-danger-filled" onclick={() => store.confirmDisconnect()} disabled={store.disconnecting}>
          {#if store.disconnecting}<span class="con-spinner"></span>{/if}
          Stop Sharing
        </button>
      </div>
    </div>
  {/if}
</dialog>

<style>
  .con-dialog[open] {
    position: fixed;
    inset: 0;
    margin: auto;
    border: none;
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    padding: 0;
    width: min(420px, calc(100vw - 32px));
    box-shadow: var(--shadow-4);
  }
  .con-dialog::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .con-dialog-body {
    display: flex;
    flex-direction: column;
    gap: 16px;
    padding: 24px;
  }
  .con-dialog-title {
    margin: 0;
    font-size: 1.125rem;
    font-weight: 500;
  }
  .con-dialog-list {
    margin: 0;
    padding: 0;
    list-style: none;
    display: flex;
    flex-direction: column;
    gap: 10px;
    font-size: 0.9rem;
    color: var(--color-text-muted);
  }
  .con-dialog-list li {
    display: flex;
    gap: 10px;
    align-items: flex-start;
  }
  .con-mark {
    flex-shrink: 0;
    font-weight: 700;
  }
  .con-mark-no {
    color: var(--color-error);
  }
  .con-mark-yes {
    color: var(--color-success);
  }
  .con-dialog-actions {
    justify-content: flex-end;
    margin-top: 4px;
  }
</style>
