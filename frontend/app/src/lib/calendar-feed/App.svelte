<script lang="ts">
  // Calendar-feed card (quest 17e38a88): a settings surface where any household
  // member creates, copies, rotates, and revokes the household meal-plan feed
  // link. Fronts /api/meal-plan/calendar-token (fca#114). Loads the status ONCE;
  // no polling (a settings surface, parity with the connections island).
  import { untrack } from 'svelte';
  import type { ShellContext } from './lib/types';
  import { feedStore as store } from './lib/feedStore.svelte';
  import {
    CLIENT_HOWTO,
    REVOKE_CONFIRM,
    ROTATE_CONFIRM,
    describeCreatedAt,
    postIsRotate,
    primaryActionLabel,
    statusLine,
  } from './lib/feed-ops';
  import ConfirmDialog from '$lib/shared/ConfirmDialog.svelte';

  let { ctx }: { ctx: ShellContext } = $props();

  // ⚠ Loop safety (memory svelte5-setup-effect-async-loader-loop): untrack() gives
  // the one-time setup effect zero reactive deps so it runs exactly once.
  $effect(() => {
    untrack(() => {
      void ctx; // household resolved server-side from the cookie
      void store.load();
    });
  });

  function onPrimary(): void {
    if (postIsRotate(store.phase)) {
      store.confirm = 'rotate';
    } else {
      void store.createOrRotate();
    }
  }
</script>

<div class="cal-page">
  <h1 class="cal-title">Calendar Feed</h1>

  {#if !store.loaded && store.loading}
    <div class="cal-loading">Loading…</div>
  {:else if !store.loaded && store.error}
    <div class="cal-inline-error" role="alert">
      <span>{store.error}</span>
      <button type="button" class="cal-btn-outline" onclick={() => void store.load()}>Retry</button>
    </div>
  {:else}
    <section class="cal-card">
      <h2 class="cal-card-title">Subscribe to the meal plan</h2>
      <p class="cal-card-sub">
        Add the household meal plan to your phone or calendar app. It shows the current week and the next three,
        one all-day event per meal, and updates on its own. The link is shared by the whole household — anyone
        in the household can rotate or revoke it.
      </p>

      <p class="cal-status" data-phase={store.phase.kind}>{statusLine(store.phase)}</p>
      {#if store.phase.kind !== 'none' && store.phase.createdAt}
        <p class="cal-created">{describeCreatedAt(store.phase.createdAt)}</p>
      {/if}

      {#if store.url}
        <div class="cal-reveal">
          <input class="cal-url" type="text" readonly value={store.url} aria-label="Subscription link" onfocus={(e) => (e.currentTarget as HTMLInputElement).select()} />
          <button type="button" class="cal-btn-primary" onclick={() => void store.copyUrl()}>📋 Copy link</button>
        </div>
      {/if}

      <div class="cal-actions">
        <button type="button" class={store.url ? 'cal-btn-outline' : 'cal-btn-primary'} disabled={store.busy} onclick={onPrimary}>
          {primaryActionLabel(store.phase)}
        </button>
        {#if store.phase.kind !== 'none'}
          <button type="button" class="cal-btn-danger" disabled={store.busy} onclick={() => (store.confirm = 'revoke')}>Revoke link</button>
        {/if}
      </div>
    </section>

    <section class="cal-card">
      <h2 class="cal-card-title">How to add it</h2>
      <ul class="cal-howto">
        {#each CLIENT_HOWTO as row (row.client)}
          <li><strong>{row.client}:</strong> {row.steps}</li>
        {/each}
      </ul>
    </section>
  {/if}
</div>

<ConfirmDialog
  open={store.confirm === 'rotate'}
  danger
  title={ROTATE_CONFIRM.title}
  message={ROTATE_CONFIRM.message}
  confirmLabel={ROTATE_CONFIRM.confirmLabel}
  onCancel={() => (store.confirm = null)}
  onConfirm={() => void store.createOrRotate()}
/>
<ConfirmDialog
  open={store.confirm === 'revoke'}
  danger
  title={REVOKE_CONFIRM.title}
  message={REVOKE_CONFIRM.message}
  confirmLabel={REVOKE_CONFIRM.confirmLabel}
  onCancel={() => (store.confirm = null)}
  onConfirm={() => void store.revoke()}
/>

<style>
  .cal-page {
    max-width: 720px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .cal-title {
    margin: 0 0 24px;
    font-size: 1.5rem;
    font-weight: 500;
  }
  .cal-loading,
  .cal-inline-error {
    padding: 24px;
    color: var(--color-text-muted, #666);
  }
  .cal-inline-error {
    display: flex;
    gap: 12px;
    align-items: center;
    color: var(--color-error);
  }
  .cal-card {
    background: var(--color-surface);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-2);
    padding: 24px;
    margin-bottom: 24px;
  }
  .cal-card-title {
    margin: 0 0 8px;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .cal-card-sub,
  .cal-status,
  .cal-created {
    margin: 0 0 16px;
    color: var(--color-text-muted);
    font-size: 0.95rem;
  }
  .cal-status[data-phase='revealed'] {
    color: var(--color-text);
    font-weight: 500;
  }
  .cal-created {
    margin-top: -8px;
    font-size: 0.85rem;
  }
  .cal-reveal {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
    align-items: center;
    margin-bottom: 16px;
  }
  .cal-url {
    flex: 1 1 320px;
    min-width: 0;
    font-family: 'Courier New', monospace;
    font-size: 0.9rem;
    padding: 10px 12px;
    border: 1px solid var(--color-line);
    border-radius: var(--radius-sm, 6px);
    background: var(--color-background);
    color: var(--color-text);
  }
  .cal-actions {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
  }
  .cal-btn-primary,
  .cal-btn-outline,
  .cal-btn-danger {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    padding: 10px 16px;
    border-radius: var(--radius-sm, 6px);
    font: inherit;
    cursor: pointer;
    border: 1px solid transparent;
  }
  .cal-btn-primary {
    background: var(--color-primary);
    color: #fff;
  }
  .cal-btn-primary:hover {
    background: var(--color-primary-hover);
  }
  .cal-btn-outline {
    background: transparent;
    color: var(--color-primary);
    border-color: var(--color-primary);
  }
  .cal-btn-danger {
    background: transparent;
    color: var(--color-error);
    border-color: var(--color-error);
  }
  .cal-btn-primary:disabled,
  .cal-btn-outline:disabled,
  .cal-btn-danger:disabled {
    opacity: 0.6;
    cursor: default;
  }
  .cal-howto {
    margin: 0;
    padding-left: 20px;
    color: var(--color-text);
    font-size: 0.95rem;
    line-height: 1.5;
  }
  .cal-howto li + li {
    margin-top: 8px;
  }
</style>
