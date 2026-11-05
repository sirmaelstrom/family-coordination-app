<script lang="ts">
  // Settings → Agent access (quest 8bfa42cf): the member mints, lists and revokes their OWN personal access tokens
  // for agent clients (Claude Code, Codex) that call the app's /mcp endpoint. Fronts /api/agent-tokens.
  // The secret is shown once, right after minting; the list never carries it. No polling (a settings surface).
  import { untrack } from 'svelte';
  import type { AgentAccess, AgentTokenDto, ShellContext } from './lib/types';
  import { ApiError, listAgentTokens, mintAgentToken, revokeAgentToken } from './lib/api';
  import {
    DEFAULT_EXPIRY_DAYS,
    accessLabel,
    clientSetup,
    describeToken,
    formatDate,
    mintBody,
    reauthUrl,
    type ExpiryChoice,
  } from './lib/ops';
  import ConfirmDialog from '$lib/shared/ConfirmDialog.svelte';
  import { showToast } from '$lib/shared/toast-store.svelte';

  let { ctx }: { ctx: ShellContext } = $props();

  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let tokens = $state<AgentTokenDto[]>([]);
  let householdName = $state<string | null>(null);
  let presets = $state<number[]>([30, 90, 365]);
  let maxLifetimeDays = $state(365);

  let label = $state('');
  let access = $state<AgentAccess>('readWrite');
  let expiryMode = $state<string>(String(DEFAULT_EXPIRY_DAYS)); // a preset's days, or 'custom'
  let customDate = $state('');
  let formError = $state<string | null>(null);
  let needsReauth = $state(false);
  let busy = $state(false);

  /** The just-minted secret. Forgotten on dismiss, reload, or the next mint. */
  let revealed = $state<{ token: string; label: string; expiresAt: string } | null>(null);
  let confirmRevoke = $state<AgentTokenDto | null>(null);

  const origin = typeof window === 'undefined' ? '' : window.location.origin;
  const setup = clientSetup(origin);

  function localDate(offsetDays: number): string {
    const d = new Date();
    d.setDate(d.getDate() + offsetDays);
    const mm = String(d.getMonth() + 1).padStart(2, '0');
    const dd = String(d.getDate()).padStart(2, '0');
    return `${d.getFullYear()}-${mm}-${dd}`;
  }
  const minDate = localDate(1);
  const maxDate = $derived(localDate(maxLifetimeDays));

  const activeTokens = $derived(tokens.filter((t) => t.status === 'active'));
  const pastTokens = $derived(tokens.filter((t) => t.status !== 'active'));

  async function load(): Promise<void> {
    loading = true;
    loadError = null;
    try {
      const list = await listAgentTokens();
      tokens = list.tokens;
      householdName = list.householdName;
      presets = list.expiryPresetDays;
      maxLifetimeDays = list.maxLifetimeDays;
    } catch (e) {
      loadError = e instanceof ApiError ? e.message : 'Could not load your agent tokens.';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    untrack(() => {
      void ctx; // household + member resolved server-side from the cookie
      void load();
    });
  });

  async function mint(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    formError = null;
    needsReauth = false;
    const expiry: ExpiryChoice =
      expiryMode === 'custom' ? { kind: 'date', date: customDate } : { kind: 'days', days: Number(expiryMode) };
    const built = mintBody(label, access, expiry);
    if ('error' in built) {
      formError = built.error;
      return;
    }
    busy = true;
    revealed = null;
    try {
      const minted = await mintAgentToken(built.body);
      revealed = { token: minted.token, label: minted.label, expiresAt: minted.expiresAt };
      label = '';
      await load();
    } catch (e) {
      if (e instanceof ApiError && e.status === 403) {
        needsReauth = true;
      } else {
        formError = e instanceof ApiError ? e.message : 'The token was not created. Try again.';
      }
    } finally {
      busy = false;
    }
  }

  async function copySecret(): Promise<void> {
    if (!revealed) return;
    try {
      await navigator.clipboard.writeText(revealed.token);
      showToast({ message: 'Token copied. Paste it where your agent reads FAMILYAPP_TOKEN.', kind: 'success' });
    } catch {
      showToast({ message: 'Unable to copy — select the token and copy it manually', kind: 'error' });
    }
  }

  async function revoke(token: AgentTokenDto): Promise<void> {
    confirmRevoke = null;
    busy = true;
    try {
      await revokeAgentToken(token.id);
      showToast({ message: `“${token.label}” revoked. Its next call will be refused.`, kind: 'success' });
    } catch (e) {
      showToast({ message: e instanceof ApiError ? e.message : 'The token was not revoked. Try again.', kind: 'error' });
    } finally {
      busy = false;
      await load();
    }
  }
</script>

<div class="aa-page">
  <h1 class="aa-title">Agent access</h1>

  <section class="aa-card">
    <h2 class="aa-card-title">Create a token</h2>
    <p class="aa-card-sub">
      A token lets an AI agent such as Claude Code or Codex read and plan the recipes, meal plan and shopping lists of
      {householdName ?? 'your household'} as you. It cannot delete anything, change members or settings, or create tokens. Make one per client so you can
      revoke them separately.
    </p>

    <form class="aa-form" onsubmit={mint}>
      <label class="aa-field">
        <span>Name</span>
        <input type="text" maxlength="100" placeholder="e.g. Claude Code on my laptop" bind:value={label} data-testid="aa-label" />
      </label>

      <fieldset class="aa-field">
        <legend>Access</legend>
        <label class="aa-radio"><input type="radio" name="aa-access" value="readWrite" bind:group={access} /> Read and write — add recipes, plan meals, build lists</label>
        <label class="aa-radio"><input type="radio" name="aa-access" value="read" bind:group={access} /> Read only</label>
      </fieldset>

      <label class="aa-field">
        <span>Expires</span>
        <select bind:value={expiryMode} data-testid="aa-expiry">
          {#each presets as days (days)}
            <option value={String(days)}>In {days} days</option>
          {/each}
          <option value="custom">On a date…</option>
        </select>
      </label>
      {#if expiryMode === 'custom'}
        <label class="aa-field">
          <span>Expiry date</span>
          <input type="date" min={minDate} max={maxDate} bind:value={customDate} />
        </label>
      {/if}

      {#if formError}<p class="aa-error" role="alert">{formError}</p>{/if}
      {#if needsReauth}
        <p class="aa-error" role="alert">
          For your security, sign in again before creating a token.
          <a href={reauthUrl()}>Sign in again</a>
        </p>
      {/if}

      <div class="aa-actions">
        <button type="submit" class="aa-btn-primary" disabled={busy} data-testid="aa-create">Create token</button>
      </div>
    </form>

    {#if revealed}
      <div class="aa-reveal" data-testid="aa-reveal">
        <p class="aa-reveal-head">
          <strong>Copy “{revealed.label}” now.</strong> It is shown only once; the app keeps only a fingerprint of it. It
          expires {formatDate(revealed.expiresAt)}.
        </p>
        <div class="aa-reveal-row">
          <input class="aa-secret" type="text" readonly value={revealed.token} aria-label="New agent token" onfocus={(e) => (e.currentTarget as HTMLInputElement).select()} />
          <button type="button" class="aa-btn-primary" onclick={() => void copySecret()}>📋 Copy</button>
          <button type="button" class="aa-btn-outline" onclick={() => (revealed = null)}>Done</button>
        </div>
        <p class="aa-hint">Store it in the <code>FAMILYAPP_TOKEN</code> environment variable on the machine that runs the agent, then:</p>
        {#each setup as row (row.client)}
          <p class="aa-hint"><strong>{row.client}</strong></p>
          <pre class="aa-code">{row.text}</pre>
        {/each}
      </div>
    {/if}
  </section>

  <section class="aa-card" data-testid="aa-tokens">
    <h2 class="aa-card-title">Your tokens</h2>
    {#if loading && tokens.length === 0}
      <p class="aa-muted">Loading…</p>
    {:else if loadError}
      <div class="aa-error" role="alert">
        <span>{loadError}</span>
        <button type="button" class="aa-btn-outline" onclick={() => void load()}>Retry</button>
      </div>
    {:else if activeTokens.length === 0 && pastTokens.length === 0}
      <p class="aa-muted">You have no agent tokens.</p>
    {:else}
      <ul class="aa-list">
        {#each [...activeTokens, ...pastTokens] as token (token.id)}
          <li class="aa-row" data-status={token.status}>
            <div class="aa-row-main">
              <span class="aa-row-label">{token.label}</span>
              <span class="aa-chip">{accessLabel(token.access)}</span>
              <code class="aa-prefix">{token.safePrefix}…</code>
              <span class="aa-muted aa-row-sub">{describeToken(token)}</span>
            </div>
            {#if token.status === 'active'}
              <button type="button" class="aa-btn-danger" disabled={busy} onclick={() => (confirmRevoke = token)}>Revoke</button>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </section>
</div>

<ConfirmDialog
  open={confirmRevoke !== null}
  danger
  title="Revoke this token?"
  message={confirmRevoke ? `“${confirmRevoke.label}” stops working on its next call. This can't be undone; you can create a new token.` : ''}
  confirmLabel="Revoke"
  onCancel={() => (confirmRevoke = null)}
  onConfirm={() => confirmRevoke && void revoke(confirmRevoke)}
/>

<style>
  .aa-page {
    max-width: 720px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .aa-title {
    margin: 0 0 24px;
    font-size: 1.5rem;
    font-weight: 500;
  }
  .aa-card {
    background: var(--color-surface);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-2);
    padding: 24px;
    margin-bottom: 24px;
  }
  .aa-card-title {
    margin: 0 0 8px;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .aa-card-sub,
  .aa-muted,
  .aa-hint {
    margin: 0 0 16px;
    color: var(--color-text-muted);
    font-size: 0.95rem;
  }
  .aa-form {
    display: flex;
    flex-direction: column;
    gap: 16px;
  }
  .aa-field {
    display: flex;
    flex-direction: column;
    gap: 6px;
    border: 0;
    padding: 0;
    margin: 0;
  }
  .aa-field > span,
  .aa-field > legend {
    font-weight: 500;
    font-size: 0.9rem;
    padding: 0;
  }
  .aa-field input[type='text'],
  .aa-field input[type='date'],
  .aa-field select {
    font: inherit;
    font-size: 1rem; /* 16px: no iOS zoom on focus */
    padding: 10px 12px;
    border: 1px solid var(--color-line);
    border-radius: var(--radius-sm, 6px);
    background: var(--color-background);
    color: var(--color-text);
  }
  .aa-radio {
    display: flex;
    gap: 8px;
    align-items: center;
  }
  .aa-error {
    margin: 0;
    color: var(--color-error);
    display: flex;
    gap: 12px;
    align-items: center;
    flex-wrap: wrap;
  }
  .aa-actions {
    display: flex;
    gap: 8px;
  }
  .aa-reveal {
    margin-top: 20px;
    padding: 16px;
    border: 1px solid var(--color-primary);
    border-radius: var(--radius-sm, 6px);
  }
  .aa-reveal-head {
    margin: 0 0 12px;
  }
  .aa-reveal-row {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
    margin-bottom: 12px;
  }
  .aa-secret {
    flex: 1 1 320px;
    min-width: 0;
    font-family: 'Courier New', monospace;
    font-size: 1rem;
    padding: 10px 12px;
    border: 1px solid var(--color-line);
    border-radius: var(--radius-sm, 6px);
    background: var(--color-background);
    color: var(--color-text);
  }
  .aa-code {
    margin: 0 0 12px;
    padding: 10px 12px;
    background: var(--color-background);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-sm, 6px);
    font-size: 0.85rem;
    white-space: pre-wrap;
    word-break: break-all;
  }
  .aa-list {
    list-style: none;
    margin: 0;
    padding: 0;
  }
  .aa-row {
    display: flex;
    gap: 12px;
    align-items: center;
    justify-content: space-between;
    padding: 12px 0;
    border-top: 1px solid var(--color-line);
  }
  .aa-row[data-status='revoked'],
  .aa-row[data-status='expired'] {
    opacity: 0.6;
  }
  .aa-row-main {
    display: flex;
    flex-wrap: wrap;
    gap: 6px 10px;
    align-items: baseline;
    min-width: 0;
  }
  .aa-row-label {
    font-weight: 500;
  }
  .aa-row-sub {
    flex-basis: 100%;
    margin: 0;
    font-size: 0.85rem;
  }
  .aa-chip {
    font-size: 0.8rem;
    padding: 2px 8px;
    border-radius: 999px;
    border: 1px solid var(--color-line);
  }
  .aa-prefix {
    font-size: 0.85rem;
    color: var(--color-text-muted);
  }
  .aa-btn-primary,
  .aa-btn-outline,
  .aa-btn-danger {
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
  .aa-btn-primary {
    background: var(--color-primary);
    color: #fff;
  }
  .aa-btn-primary:hover {
    background: var(--color-primary-hover);
  }
  .aa-btn-outline {
    background: transparent;
    color: var(--color-primary);
    border-color: var(--color-primary);
  }
  .aa-btn-danger {
    background: transparent;
    color: var(--color-error);
    border-color: var(--color-error);
    flex-shrink: 0;
  }
  .aa-btn-primary:disabled,
  .aa-btn-outline:disabled,
  .aa-btn-danger:disabled {
    opacity: 0.6;
    cursor: default;
  }
</style>
