<script lang="ts">
  // Enter-a-code section (parity Connections.razor:84-151): input (uppercase/strip/
  // max-6) + Submit → validate; on valid, a pre-connection confirm → Connect/Cancel.
  // On accept failure the store returns here with codeError (review R-B3).
  import { connectionsStore as store } from '../connectionsStore.svelte';

  function onInput(e: Event) {
    store.setCode((e.currentTarget as HTMLInputElement).value);
  }
  function onKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' && store.canSubmit) void store.validate();
  }
</script>

<section class="con-card">
  <h2 class="con-card-title">Enter a Code</h2>
  <p class="con-card-sub">Got a code from someone? Enter it below.</p>

  {#if !store.showConfirm}
    <div class="con-entry-row">
      <input
        class="con-code-input"
        type="text"
        autocomplete="off"
        autocapitalize="characters"
        spellcheck="false"
        maxlength="6"
        aria-label="Invite Code"
        placeholder="ABC123"
        value={store.enteredCode}
        oninput={onInput}
        onkeydown={onKeydown}
        disabled={store.validating}
      />
      <button type="button" class="con-btn-primary" onclick={() => store.validate()} disabled={!store.canSubmit}>
        {#if store.validating}<span class="con-spinner"></span>{/if}
        Submit
      </button>
    </div>

    {#if store.codeError}
      <div class="con-alert con-alert-warning" role="alert" style="margin-top: 12px;">{store.codeError}</div>
    {/if}
  {:else}
    <div class="con-alert con-alert-info" style="margin-bottom: 16px;">
      Connecting with <strong>{store.pendingHouseholdName}</strong> lets both families browse each other's recipes.
      They can see your recipes but can't change them. You can disconnect anytime.
    </div>
    <div class="con-actions">
      <button type="button" class="con-btn-outline" onclick={() => store.cancelConfirm()} disabled={store.accepting}>
        Cancel
      </button>
      <button type="button" class="con-btn-primary" onclick={() => store.accept()} disabled={store.accepting}>
        {#if store.accepting}<span class="con-spinner"></span>{/if}
        Connect
      </button>
    </div>
  {/if}
</section>
