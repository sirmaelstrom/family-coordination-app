<script lang="ts">
  // Calendar-feed card (quest 17e38a88): a settings surface where each household
  // member creates, copies, rotates, and revokes their OWN meal-plan feed link
  // (per-member links, quest 8dba87f4), and can revoke the legacy shared link while
  // one is active. Fronts /api/meal-plan/calendar-token (fca#114). A second card does the
  // same for the member's chores feed (/api/chores/calendar-token, quest 3bdbcab7). Loads each status ONCE;
  // no polling (a settings surface, parity with the connections island).
  import { untrack } from 'svelte';
  import type { ShellContext } from './lib/types';
  import { feedStore as store, choresFeedStore as chores, type FeedStore } from './lib/feedStore.svelte';
  import {
    CHORES_REVOKE_CONFIRM,
    CHORES_ROTATE_CONFIRM,
    CLIENT_HOWTO,
    REVOKE_CONFIRM,
    REVOKE_SHARED_CONFIRM,
    ROTATE_CONFIRM,
    describeCreatedAt,
    postIsRotate,
    primaryActionLabel,
    sharedLinkLine,
    statusLine,
    type FeedKind,
  } from './lib/feed-ops';
  import ConfirmDialog from '$lib/shared/ConfirmDialog.svelte';

  let { ctx }: { ctx: ShellContext } = $props();

  /** The legacy shared-link block's copy, or null while no shared link is active. */
  const sharedLine = $derived(sharedLinkLine({ sharedLinkActive: store.shared.active }));

  // ⚠ Loop safety (memory svelte5-setup-effect-async-loader-loop): untrack() gives
  // the one-time setup effect zero reactive deps so it runs exactly once.
  $effect(() => {
    untrack(() => {
      void ctx; // household resolved server-side from the cookie
      void store.load();
      void chores.load();
    });
  });

  function onPrimary(s: FeedStore): void {
    if (postIsRotate(s.phase)) {
      s.confirm = 'rotate';
    } else {
      void s.createOrRotate();
    }
  }
</script>

{#snippet feedBody(s: FeedStore, kind: FeedKind)}
  {#if !s.loaded && s.loading}
    <p class="cal-loading">Loading…</p>
  {:else if !s.loaded && s.error}
    <div class="cal-inline-error" role="alert">
      <span>{s.error}</span>
      <button type="button" class="cal-btn-outline" onclick={() => void s.load()}>Retry</button>
    </div>
  {:else}
    {@render feedControls(s, kind)}
  {/if}
{/snippet}

{#snippet feedControls(s: FeedStore, kind: FeedKind)}
  <p class="cal-status" data-phase={s.phase.kind}>{statusLine(s.phase, kind)}</p>
  {#if s.phase.kind !== 'none' && s.phase.createdAt}
    <p class="cal-created">{describeCreatedAt(s.phase.createdAt)}</p>
  {/if}

  {#if s.url}
    <div class="cal-reveal">
      <input class="cal-url" type="text" readonly value={s.url} aria-label={kind === 'chores' ? 'Chores subscription link' : 'Meal plan subscription link'} onfocus={(e) => (e.currentTarget as HTMLInputElement).select()} />
      <button type="button" class="cal-btn-primary" onclick={() => void s.copyUrl()}>📋 Copy link</button>
    </div>
  {/if}

  <div class="cal-actions">
    <button type="button" class={s.url ? 'cal-btn-outline' : 'cal-btn-primary'} disabled={s.busy} onclick={() => onPrimary(s)}>
      {primaryActionLabel(s.phase)}
    </button>
    {#if s.phase.kind !== 'none'}
      <button type="button" class="cal-btn-danger" disabled={s.busy} onclick={() => (s.confirm = 'revoke')}>Revoke link</button>
    {/if}
  </div>
{/snippet}

<div class="cal-page">
  <h1 class="cal-title">Calendar Feed</h1>

  <!-- Each card has its own loading/error boundary: one feed's failed status load must not hide the other's controls. -->
  <section class="cal-card" data-testid="cal-meal-feed">
    <h2 class="cal-card-title">Subscribe to the meal plan</h2>
    <p class="cal-card-sub">
      Add the household meal plan to your phone or calendar app. It shows the current week and the next three,
      one all-day event per meal, and updates on its own. The link is yours: every member has their own, and
      rotating or revoking yours does not affect anyone else's.
    </p>
    {@render feedBody(store, 'meal')}
  </section>

  <section class="cal-card" data-testid="cal-chores-feed">
    <h2 class="cal-card-title">Subscribe to your chores</h2>
    <p class="cal-card-sub">
      Add the chores assigned to you, or that you claimed, to your calendar: one all-day event per chore on the day
      it is next due. An overdue chore shows on today. A chore that repeats some days after it was last done moves
      when it is done again. Your calendar app refreshes on its own schedule, so it can be up to a day behind the app.
    </p>
    {@render feedBody(chores, 'chores')}
  </section>

  {#if sharedLine}
    <section class="cal-card" data-testid="cal-shared-link">
      <h2 class="cal-card-title">Older shared link</h2>
      <p class="cal-status">{sharedLine}</p>
      {#if store.shared.createdAt}
        <p class="cal-created">{describeCreatedAt(store.shared.createdAt)}</p>
      {/if}
      <div class="cal-actions">
        <button type="button" class="cal-btn-danger" disabled={store.busy} onclick={() => (store.confirm = 'revoke-shared')}>
          Revoke shared link
        </button>
      </div>
    </section>
  {/if}

  <section class="cal-card">
    <h2 class="cal-card-title">How to add it</h2>
    <ul class="cal-howto">
      {#each CLIENT_HOWTO as row (row.client)}
        <li><strong>{row.client}:</strong> {row.steps}</li>
      {/each}
    </ul>
  </section>
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
<ConfirmDialog
  open={chores.confirm === 'rotate'}
  danger
  title={CHORES_ROTATE_CONFIRM.title}
  message={CHORES_ROTATE_CONFIRM.message}
  confirmLabel={CHORES_ROTATE_CONFIRM.confirmLabel}
  onCancel={() => (chores.confirm = null)}
  onConfirm={() => void chores.createOrRotate()}
/>
<ConfirmDialog
  open={chores.confirm === 'revoke'}
  danger
  title={CHORES_REVOKE_CONFIRM.title}
  message={CHORES_REVOKE_CONFIRM.message}
  confirmLabel={CHORES_REVOKE_CONFIRM.confirmLabel}
  onCancel={() => (chores.confirm = null)}
  onConfirm={() => void chores.revoke()}
/>
<ConfirmDialog
  open={store.confirm === 'revoke-shared'}
  danger
  title={REVOKE_SHARED_CONFIRM.title}
  message={REVOKE_SHARED_CONFIRM.message}
  confirmLabel={REVOKE_SHARED_CONFIRM.confirmLabel}
  onCancel={() => (store.confirm = null)}
  onConfirm={() => void store.revokeShared()}
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
