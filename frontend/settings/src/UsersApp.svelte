<script lang="ts">
  // Manage Users view (#settings-users-root). Parity with WhitelistAdmin.razor:
  // add-by-email, a member list with "You" + status, and enable/disable/delete.
  // Button visibility mirrors the server guards (self / last-active / last-user);
  // the server is the source of truth (review R-A2) — the client gate is cosmetic.
  import { onMount } from 'svelte';
  import type { ShellContext, MemberDto } from './lib/types';
  import { membersStore } from './lib/membersStore.svelte';
  import ConfirmDialog from './lib/components/ConfirmDialog.svelte';
  import Toasts from './lib/components/Toasts.svelte';

  let { ctx }: { ctx: ShellContext } = $props();

  const store = membersStore;

  onMount(() => {
    void ctx; // host context available; not needed by the store
    void store.load();
  });

  let newEmail = $state('');
  async function add() {
    if (!newEmail.trim()) return;
    if (await store.add(newEmail)) newEmail = '';
  }

  function isSelf(m: MemberDto): boolean {
    return m.userId === store.currentUserId;
  }
  // Disable is offered only for an active member while more than one remains active.
  function canDisable(m: MemberDto): boolean {
    return !isSelf(m) && m.isWhitelisted && store.activeCount > 1;
  }
  function canEnable(m: MemberDto): boolean {
    return !isSelf(m) && !m.isWhitelisted;
  }
  // Delete is offered for a non-self member while more than one user remains.
  function canDelete(m: MemberDto): boolean {
    return !isSelf(m) && store.members.length > 1;
  }

  // Delete confirm.
  let confirmMember = $state<MemberDto | null>(null);
  let confirmMessage = $state('');
  function askDelete(m: MemberDto) {
    confirmMessage =
      `Permanently delete ${m.displayName ?? m.email} (${m.email})? ` +
      `This removes their account from the household. Their recipes and feedback are kept. This can't be undone.`;
    confirmMember = m;
  }
  async function confirmDelete() {
    const target = confirmMember;
    confirmMember = null;
    if (target) await store.remove(target.userId);
  }
</script>

<div class="set-page">
  <h1 class="set-title">Manage Family Members</h1>

  {#if store.loading}
    <div class="set-skeleton">Loading…</div>
  {:else if store.error}
    <div class="set-error">{store.error}</div>
  {:else}
    <!-- Add member -->
    <section class="set-card">
      <h2 class="set-card-title">Add Family Member</h2>
      <div class="set-add-row">
        <input
          class="set-input set-grow"
          type="email"
          placeholder="email@example.com"
          bind:value={newEmail}
          onkeydown={(e) => e.key === 'Enter' && add()}
        />
        <button type="button" class="set-btn-primary" onclick={add}>Add</button>
      </div>
    </section>

    <!-- Members -->
    <section class="set-card">
      <h2 class="set-card-title">Current Members</h2>
      <div class="set-members">
        {#each store.members as m (m.userId)}
          <div class="set-row set-row-static">
            <div class="set-member-info">
              <div class="set-member-line">
                <span class="set-member-email">{m.email}</span>
                {#if isSelf(m)}
                  <span class="set-chip set-chip-info">You</span>
                {/if}
                {#if m.isWhitelisted}
                  <span class="set-chip set-chip-success">Active</span>
                {:else}
                  <span class="set-chip set-chip-muted">Disabled</span>
                {/if}
              </div>
              {#if m.displayName}
                <span class="set-member-name">{m.displayName}</span>
              {/if}
            </div>
            <div class="set-row-actions">
              {#if isSelf(m)}
                <span class="set-muted-note">Cannot modify self</span>
              {:else}
                {#if canDisable(m)}
                  <button type="button" class="set-btn-text set-warn" onclick={() => store.toggle(m.userId, false)}>Disable</button>
                {:else if canEnable(m)}
                  <button type="button" class="set-btn-text set-ok" onclick={() => store.toggle(m.userId, true)}>Enable</button>
                {/if}
                {#if canDelete(m)}
                  <button type="button" class="set-btn-text set-danger-text" onclick={() => askDelete(m)}>Delete</button>
                {/if}
              {/if}
            </div>
          </div>
        {/each}
      </div>
    </section>
  {/if}
</div>

<ConfirmDialog
  open={confirmMember != null}
  title="Delete User"
  message={confirmMessage}
  confirmLabel="Delete"
  onCancel={() => (confirmMember = null)}
  onConfirm={confirmDelete}
/>
<Toasts />

<style>
  .set-page {
    max-width: 720px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .set-title {
    margin: 0 0 20px;
    font-size: 1.5rem;
    font-weight: 500;
  }
  .set-card {
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-1);
    padding: 16px;
    margin-bottom: 16px;
  }
  .set-card-title {
    margin: 0 0 12px;
    font-size: 1rem;
    font-weight: 500;
  }
  .set-add-row {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
    align-items: center;
  }
  .set-input {
    font: inherit;
    padding: 10px 12px;
    border-radius: var(--radius-sm);
    border: 1px solid var(--color-line-strong);
    background: var(--color-surface);
    color: var(--color-text);
    min-width: 120px;
  }
  .set-input:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
  }
  .set-grow {
    flex: 1;
    min-width: 200px;
  }
  .set-btn-primary {
    font: inherit;
    font-weight: 500;
    padding: 10px 20px;
    min-height: 42px;
    border: none;
    border-radius: var(--radius-sm);
    background: var(--color-primary);
    color: #fff;
    cursor: pointer;
  }
  .set-btn-primary:hover {
    background: var(--color-primary-hover);
  }
  .set-members {
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .set-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 10px 12px;
    border: 1px solid var(--color-line);
    border-radius: var(--radius-sm);
  }
  .set-row-static {
    background: var(--color-surface);
  }
  .set-member-info {
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }
  .set-member-line {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
  }
  .set-member-email {
    overflow-wrap: anywhere;
  }
  .set-member-name {
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .set-chip {
    font-size: 0.6875rem;
    font-weight: 500;
    padding: 2px 10px;
    border-radius: 12px;
    white-space: nowrap;
  }
  .set-chip-info {
    border: 1px solid var(--color-info);
    color: var(--color-info);
  }
  .set-chip-success {
    border: 1px solid var(--color-success);
    color: var(--color-success);
  }
  .set-chip-muted {
    border: 1px solid var(--color-line-strong);
    color: var(--color-text-muted);
  }
  .set-row-actions {
    display: flex;
    align-items: center;
    gap: 4px;
    flex-shrink: 0;
  }
  .set-btn-text {
    font: inherit;
    font-weight: 500;
    background: transparent;
    border: none;
    cursor: pointer;
    padding: 6px 10px;
    border-radius: var(--radius-sm);
    color: var(--color-primary);
  }
  .set-btn-text:hover {
    background: var(--color-action-hover);
  }
  .set-warn {
    color: var(--color-warning);
  }
  .set-ok {
    color: var(--color-success);
  }
  .set-danger-text {
    color: var(--color-error);
  }
  .set-muted-note {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .set-skeleton,
  .set-error {
    padding: 16px;
    border-radius: var(--radius-sm);
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .set-error {
    background: rgba(229, 57, 53, 0.08);
    border-left: 4px solid var(--color-error);
    color: var(--color-text);
  }
</style>
