<script lang="ts">
  // Feedback view (#admin-feedback-root). Parity with FeedbackAdmin.razor: DUAL-MODE
  // (the server scopes the list — admin sees all, a regular user sees own household, R-C1),
  // an all/unread/open filter, type chip + New/Resolved chips + author line, and per-item
  // mark-read / resolve / reopen. Polls at 15s while visible (R-C9).
  import { onMount } from 'svelte';
  import type { ShellContext, FeedbackDto, FeedbackType } from './lib/types';
  import { feedbackStore } from './lib/feedbackStore.svelte';
  import { formatDateTime } from './lib/dates';
  import { startLiveness } from './lib/liveness';

  let { ctx }: { ctx: ShellContext } = $props();
  const store = feedbackStore;

  // Feedback type label/icon/color map (R-C10 — the enum is camelCase on the wire; the chip
  // shows a friendly label, not the raw value).
  const TYPE_META: Record<FeedbackType, { label: string; icon: string; cls: string }> = {
    bug: { label: 'Bug', icon: '🐛', cls: 'adm-type-bug' },
    featureRequest: { label: 'Feature Request', icon: '💡', cls: 'adm-type-feature' },
    general: { label: 'General', icon: '💬', cls: 'adm-type-general' },
  };
  function typeMeta(t: FeedbackType) {
    return TYPE_META[t] ?? { label: t, icon: '💬', cls: 'adm-type-general' };
  }

  let filter = $state<'all' | 'unread' | 'open'>('all');
  const filtered = $derived(
    filter === 'unread'
      ? store.items.filter((i) => !i.isRead)
      : filter === 'open'
        ? store.items.filter((i) => !i.isResolved)
        : store.items,
  );

  onMount(() => {
    void ctx;
    void store.load();
    const handle = startLiveness(() => store.load(), 15_000);
    return () => handle.stop();
  });

  /** Author suffix (R-C6): live user → name; deleted → "Deleted user"; anonymous → nothing. */
  function authorSuffix(item: FeedbackDto): string {
    if (item.authorName) return ` — ${item.authorName}`;
    if (item.authorDeleted) return ' — Deleted user';
    return '';
  }
</script>

<div class="adm-page">
  <div class="adm-header">
    <h1 class="adm-title">Feedback &amp; Requests</h1>
    <div class="adm-toggle" role="group" aria-label="Filter feedback">
      <button type="button" class:adm-toggle-on={filter === 'all'} onclick={() => (filter = 'all')}>All</button>
      <button type="button" class:adm-toggle-on={filter === 'unread'} onclick={() => (filter = 'unread')}>Unread</button>
      <button type="button" class:adm-toggle-on={filter === 'open'} onclick={() => (filter = 'open')}>Open</button>
    </div>
  </div>

  {#if store.loading}
    <div class="adm-skeleton">Loading…</div>
  {:else if store.error}
    <div class="adm-error">{store.error}</div>
  {:else if filtered.length === 0}
    <div class="adm-info">
      No feedback yet. The feedback button appears in the bottom-left corner of every page.
    </div>
  {:else}
    <div class="adm-cards">
      {#each filtered as item (item.id)}
        {@const meta = typeMeta(item.type)}
        <div class="adm-card" class:adm-card-unread={!item.isRead}>
          <div class="adm-card-head">
            <span class="adm-avatar {meta.cls}" aria-hidden="true">{meta.icon}</span>
            <div class="adm-card-main">
              <div class="adm-card-line">
                <span class="adm-chip {meta.cls}">{meta.label}</span>
                {#if !item.isRead}
                  <span class="adm-chip adm-chip-new">New</span>
                {/if}
                {#if item.isResolved}
                  <span class="adm-chip adm-chip-resolved">Resolved</span>
                {/if}
              </div>
              <div class="adm-card-meta">{formatDateTime(item.createdAt)}{authorSuffix(item)}</div>
            </div>
            <div class="adm-card-actions">
              {#if !item.isRead}
                <button type="button" class="adm-btn-text" onclick={() => store.markRead(item.id)}>Mark read</button>
              {/if}
              {#if !item.isResolved}
                <button type="button" class="adm-btn-text adm-ok" onclick={() => store.markResolved(item.id)}>Resolve</button>
              {:else}
                <button type="button" class="adm-btn-text" onclick={() => store.reopen(item.id)}>Reopen</button>
              {/if}
            </div>
          </div>
          <div class="adm-card-body">{item.message}</div>
          {#if item.currentPage}
            <div class="adm-card-page">🔗 {item.currentPage}</div>
          {/if}
        </div>
      {/each}
    </div>
  {/if}
</div>


<style>
  .adm-page {
    max-width: 900px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .adm-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    flex-wrap: wrap;
    margin-bottom: 20px;
  }
  .adm-title {
    margin: 0;
    font-size: 1.5rem;
    font-weight: 500;
  }
  .adm-toggle {
    display: inline-flex;
    border: 1px solid var(--color-line-strong);
    border-radius: 999px;
    overflow: hidden;
  }
  .adm-toggle button {
    font: inherit;
    font-weight: 500;
    border: none;
    background: transparent;
    color: var(--color-text);
    padding: 6px 16px;
    cursor: pointer;
  }
  .adm-toggle button.adm-toggle-on {
    background: var(--color-primary);
    color: #fff;
  }
  .adm-cards {
    display: flex;
    flex-direction: column;
    gap: 12px;
  }
  .adm-card {
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-1);
    padding: 14px 16px;
  }
  .adm-card-unread {
    border-left: 4px solid var(--color-info);
  }
  .adm-card-head {
    display: flex;
    align-items: flex-start;
    gap: 12px;
  }
  .adm-avatar {
    flex-shrink: 0;
    width: 38px;
    height: 38px;
    display: grid;
    place-items: center;
    border-radius: 50%;
    border: 1px solid var(--color-line-strong);
    font-size: 1.1rem;
  }
  .adm-card-main {
    flex: 1;
    min-width: 0;
  }
  .adm-card-line {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
  }
  .adm-card-meta {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    margin-top: 4px;
  }
  .adm-card-body {
    margin-top: 10px;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
    font-size: 0.9375rem;
  }
  .adm-card-page {
    margin-top: 8px;
    font-size: 0.75rem;
    color: var(--color-text-muted);
    overflow-wrap: anywhere;
  }
  .adm-card-actions {
    display: flex;
    align-items: flex-start;
    gap: 4px;
    flex-shrink: 0;
  }
  .adm-chip {
    font-size: 0.6875rem;
    font-weight: 500;
    padding: 2px 10px;
    border-radius: 12px;
    white-space: nowrap;
    border: 1px solid var(--color-line-strong);
    color: var(--color-text-muted);
  }
  .adm-type-bug {
    border-color: var(--color-error);
    color: var(--color-error);
  }
  .adm-type-feature {
    border-color: var(--color-info);
    color: var(--color-info);
  }
  .adm-type-general {
    border-color: var(--color-line-strong);
    color: var(--color-text-muted);
  }
  .adm-chip-new {
    border-color: var(--color-info);
    color: var(--color-info);
  }
  .adm-chip-resolved {
    border-color: var(--color-success);
    color: var(--color-success);
  }
  .adm-btn-text {
    font: inherit;
    font-weight: 500;
    background: transparent;
    border: none;
    cursor: pointer;
    padding: 6px 10px;
    border-radius: var(--radius-sm);
    color: var(--color-primary);
  }
  .adm-btn-text:hover {
    background: var(--color-action-hover);
  }
  .adm-ok {
    color: var(--color-success);
  }
  .adm-skeleton,
  .adm-info,
  .adm-error {
    padding: 16px;
    border-radius: var(--radius-sm);
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .adm-error {
    background: rgba(229, 57, 53, 0.08);
    border-left: 4px solid var(--color-error);
    color: var(--color-text);
  }
  .adm-info {
    background: rgba(30, 136, 229, 0.08);
    border-left: 4px solid var(--color-info);
    color: var(--color-text);
  }
</style>
