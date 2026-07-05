<script lang="ts">
  // Household Requests view (#admin-households-root). Parity with HouseholdAdmin.razor:
  // SITE-ADMIN ONLY (a 403 on load → "Access denied", R-C4), a pending/all filter, request
  // cards with approve (confirm) / reject (reason dialog), and an existing-households table.
  // The approve transaction / already-reviewed 409 / member counts are SERVER-side (R-C2/3/8);
  // this view just renders + drives them. Polls at 30s while visible (R-C9).
  import { onMount } from 'svelte';
  import type { ShellContext, HouseholdRequestDto } from './lib/types';
  import { householdRequestsStore } from './lib/householdRequestsStore.svelte';
  import { formatDateTime, formatDate } from './lib/dates';
  import { startLiveness, type LivenessHandle } from './lib/liveness';
  import ConfirmDialog from '$lib/shared/ConfirmDialog.svelte';
  import RejectReasonDialog from './lib/components/RejectReasonDialog.svelte';

  let { ctx }: { ctx: ShellContext } = $props();
  const store = householdRequestsStore;

  let filter = $state<'pending' | 'all'>('pending');
  const filtered = $derived(
    filter === 'pending' ? store.requests.filter((r) => r.status === 'pending') : store.requests,
  );

  onMount(() => {
    void ctx; // host context available; not needed by the store
    let handle: LivenessHandle | null = null;
    void (async () => {
      await store.load();
      // Only poll once we know the caller is a site admin (parity: the timer started inside the
      // _isSiteAdmin branch). A non-admin's 403 view stays static.
      if (!store.accessDenied) {
        handle = startLiveness(() => store.load(), 30_000);
      }
    })();
    return () => handle?.stop();
  });

  function statusIcon(status: HouseholdRequestDto['status']): string {
    return status === 'approved' ? '✅' : status === 'rejected' ? '❌' : '⏳';
  }
  function statusLabel(status: HouseholdRequestDto['status']): string {
    return status === 'approved' ? 'Approved' : status === 'rejected' ? 'Rejected' : 'Pending';
  }

  // Approve confirm.
  let confirmRequest = $state<HouseholdRequestDto | null>(null);
  const approveMessage = $derived(
    confirmRequest ? `Create household “${confirmRequest.householdName}” for ${confirmRequest.displayName}?` : '',
  );
  async function confirmApprove() {
    const r = confirmRequest;
    confirmRequest = null;
    if (r) await store.approve(r.id, r.householdName);
  }

  // Reject reason dialog.
  let rejectTarget = $state<HouseholdRequestDto | null>(null);
  async function confirmReject(reason: string) {
    const r = rejectTarget;
    rejectTarget = null;
    if (r) await store.reject(r.id, reason, r.displayName);
  }

  // Invite a household (admin "push" — create the household + owner directly).
  let inviteName = $state('');
  let inviteEmail = $state('');
  let inviteDisplayName = $state('');
  const canInvite = $derived(
    inviteName.trim().length > 0 && inviteEmail.trim().length > 0 && !store.creating,
  );
  async function submitInvite(e: SubmitEvent) {
    e.preventDefault();
    if (!canInvite) return;
    const ok = await store.createHousehold(inviteName.trim(), inviteEmail.trim(), inviteDisplayName);
    if (ok) {
      inviteName = '';
      inviteEmail = '';
      inviteDisplayName = '';
    }
  }
</script>

<div class="adm-page">
  <div class="adm-header">
    <h1 class="adm-title">Household Requests</h1>
    {#if !store.accessDenied}
      <div class="adm-toggle" role="group" aria-label="Filter requests">
        <button type="button" class:adm-toggle-on={filter === 'pending'} onclick={() => (filter = 'pending')}>Pending</button>
        <button type="button" class:adm-toggle-on={filter === 'all'} onclick={() => (filter = 'all')}>All</button>
      </div>
    {/if}
  </div>

  {#if store.accessDenied}
    <div class="adm-denied">Access denied. Only site administrators can manage household requests.</div>
  {:else if store.loading}
    <div class="adm-skeleton">Loading…</div>
  {:else if store.error}
    <div class="adm-error">{store.error}</div>
  {:else}
    {#if filtered.length === 0}
      <div class="adm-info">
        {filter === 'pending' ? 'No pending household requests.' : 'No household requests yet.'}
      </div>
    {:else}
      <div class="adm-cards">
        {#each filtered as r (r.id)}
          <div class="adm-card" class:adm-card-pending={r.status === 'pending'}>
            <div class="adm-card-head">
              <span class="adm-avatar adm-status-{r.status}" aria-hidden="true">{statusIcon(r.status)}</span>
              <div class="adm-card-main">
                <div class="adm-card-line">
                  <span class="adm-card-name">{r.householdName}</span>
                  <span class="adm-chip adm-chip-{r.status}">{statusLabel(r.status)}</span>
                </div>
                <div class="adm-card-sub"><strong>{r.displayName}</strong> ({r.email})</div>
                <div class="adm-card-meta">
                  Requested: {formatDateTime(r.requestedAt)}
                  {#if r.reviewedAt}
                    • Reviewed: {formatDate(r.reviewedAt)}{#if r.reviewedBy} by {r.reviewedBy}{/if}
                  {/if}
                </div>
              </div>
              {#if r.status === 'pending'}
                <div class="adm-card-actions">
                  <button type="button" class="adm-btn-text adm-ok" onclick={() => (confirmRequest = r)}>Approve</button>
                  <button type="button" class="adm-btn-text adm-danger-text" onclick={() => (rejectTarget = r)}>Reject</button>
                </div>
              {/if}
            </div>
            {#if r.status === 'rejected' && r.rejectionReason}
              <div class="adm-card-reason"><strong>Rejection reason:</strong> {r.rejectionReason}</div>
            {/if}
          </div>
        {/each}
      </div>
    {/if}

    <h2 class="adm-subtitle">Invite a Household</h2>
    <form class="adm-form" onsubmit={submitInvite}>
      <p class="adm-form-hint">
        Create a new household and its owner. They can sign in with Google straight away — no request to approve.
      </p>
      <div class="adm-form-grid">
        <label class="adm-field">
          <span class="adm-field-label">Household name</span>
          <input class="adm-input" type="text" bind:value={inviteName} maxlength="200" placeholder="The Smiths" required />
        </label>
        <label class="adm-field">
          <span class="adm-field-label">Owner email</span>
          <input class="adm-input" type="email" bind:value={inviteEmail} maxlength="256" placeholder="owner@example.com" required />
        </label>
        <label class="adm-field">
          <span class="adm-field-label">Owner name <span class="adm-field-opt">(optional)</span></span>
          <input class="adm-input" type="text" bind:value={inviteDisplayName} maxlength="200" placeholder="Jane Smith" />
        </label>
      </div>
      <div class="adm-form-actions">
        <button type="submit" class="adm-btn-primary" disabled={!canInvite}>
          {store.creating ? 'Creating…' : 'Create household'}
        </button>
      </div>
    </form>

    <h2 class="adm-subtitle">Existing Households</h2>
    {#if store.households.length === 0}
      <div class="adm-info">No households exist yet.</div>
    {:else}
      <div class="adm-table-wrap">
        <table class="adm-table">
          <thead>
            <tr><th>Household</th><th>Members</th><th>Created</th></tr>
          </thead>
          <tbody>
            {#each store.households as h (h.householdId)}
              <tr>
                <td>{h.name}</td>
                <td>{h.memberCount}</td>
                <td>{formatDate(h.createdAt)}</td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
    {/if}
  {/if}
</div>

<ConfirmDialog
  open={confirmRequest != null}
  title="Approve Request"
  message={approveMessage}
  confirmLabel="Approve"
  tone="primary"
  onCancel={() => (confirmRequest = null)}
  onConfirm={confirmApprove}
/>
<RejectReasonDialog
  open={rejectTarget != null}
  requestorName={rejectTarget?.displayName ?? ''}
  householdName={rejectTarget?.householdName ?? ''}
  onCancel={() => (rejectTarget = null)}
  onConfirm={confirmReject}
/>

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
  .adm-subtitle {
    margin: 28px 0 12px;
    font-size: 1.125rem;
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
  .adm-card-pending {
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
  .adm-card-name {
    font-size: 1.0625rem;
    font-weight: 500;
  }
  .adm-card-sub {
    font-size: 0.875rem;
    color: var(--color-text-muted);
    margin-top: 2px;
    overflow-wrap: anywhere;
  }
  .adm-card-meta {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    margin-top: 4px;
  }
  .adm-card-actions {
    display: flex;
    align-items: flex-start;
    gap: 4px;
    flex-shrink: 0;
  }
  .adm-card-reason {
    font-size: 0.875rem;
    color: var(--color-text-muted);
    margin-top: 10px;
    padding-top: 10px;
    border-top: 1px solid var(--color-line);
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
  .adm-chip-pending {
    border-color: var(--color-info);
    color: var(--color-info);
  }
  .adm-chip-approved {
    border-color: var(--color-success);
    color: var(--color-success);
  }
  .adm-chip-rejected {
    border-color: var(--color-error);
    color: var(--color-error);
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
  .adm-danger-text {
    color: var(--color-error);
  }
  .adm-form {
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-1);
    padding: 16px;
  }
  .adm-form-hint {
    margin: 0 0 14px;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .adm-form-grid {
    display: grid;
    grid-template-columns: 1fr;
    gap: 12px;
  }
  @media (min-width: 640px) {
    .adm-form-grid {
      grid-template-columns: 1fr 1fr;
    }
  }
  .adm-field {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .adm-field-label {
    font-size: 0.8125rem;
    font-weight: 500;
    color: var(--color-text-muted);
  }
  .adm-field-opt {
    font-weight: 400;
  }
  .adm-input {
    font: inherit;
    padding: 9px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    color: var(--color-text);
  }
  .adm-input:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
  }
  .adm-form-actions {
    margin-top: 14px;
    display: flex;
    justify-content: flex-end;
  }
  .adm-btn-primary {
    font: inherit;
    font-weight: 500;
    background: var(--color-primary);
    color: #fff;
    border: none;
    border-radius: var(--radius-sm);
    padding: 9px 18px;
    cursor: pointer;
    min-height: 40px;
  }
  .adm-btn-primary:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .adm-btn-primary:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
  .adm-table-wrap {
    overflow-x: auto;
    border: 1px solid var(--color-line);
    border-radius: var(--radius-md);
  }
  .adm-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9375rem;
  }
  .adm-table th,
  .adm-table td {
    text-align: left;
    padding: 10px 14px;
    border-bottom: 1px solid var(--color-line);
  }
  .adm-table th {
    font-weight: 500;
    color: var(--color-text-muted);
    font-size: 0.8125rem;
  }
  .adm-table tbody tr:last-child td {
    border-bottom: none;
  }
  .adm-skeleton,
  .adm-info,
  .adm-denied,
  .adm-error {
    padding: 16px;
    border-radius: var(--radius-sm);
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .adm-denied,
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
