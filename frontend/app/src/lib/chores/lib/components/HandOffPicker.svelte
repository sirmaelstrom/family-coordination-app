<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Hand-off picker (WP-11). A small modal listing household members so the
  // current holder can pass a chore to someone else — or release it back to the
  // pile ("Leave for anyone"). The parent runs the store `handOff` mutation
  // (optimistic + 409 reconcile) with the chosen target (null ⇒ pile).
  // ───────────────────────────────────────────────────────────────────────
  import type { MemberDto } from '../types';
  import MemberAvatar from '$lib/shared/MemberAvatar.svelte';

  interface Props {
    open: boolean;
    /** Chore name, for the dialog heading. */
    choreName: string;
    members: MemberDto[];
    /** Hide the current holder from the list (no point handing to yourself). */
    excludeUserId?: number | null;
    /**
     * 'handoff' (default) — passing a HELD chore on, with a "Leave for anyone"
     * pile option. 'assign' — pushing an UP-FOR-GRABS chore onto someone; the
     * pile option is hidden (it's already in the pile) and the copy says "Assign".
     * Both call the SAME hand-off mechanism server-side (a member target ⇒ Assigned).
     */
    mode?: 'handoff' | 'assign';
    onClose: () => void;
    /** targetUserId null ⇒ return to the pile ("Leave for anyone"). */
    onSelect: (targetUserId: number | null) => void;
  }

  let { open, choreName, members, excludeUserId = null, mode = 'handoff', onClose, onSelect }: Props =
    $props();

  let dialogEl: HTMLDialogElement | null = $state(null);

  let isAssign = $derived(mode === 'assign');
  let heading = $derived(isAssign ? 'Assign chore' : 'Hand off');
  let subtitle = $derived(
    isAssign
      ? `Pick who should take “${choreName}”.`
      : `Pass “${choreName}” to someone — or leave it for anyone.`,
  );

  let pickable = $derived(members.filter((m) => m.userId !== excludeUserId));

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      dialogEl.showModal();
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  function choose(targetUserId: number | null) {
    onSelect(targetUserId);
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="ch-dialog">
  <div class="ch-dialog-body">
    <h2>{heading}</h2>
    <p class="ch-dialog-sub">{subtitle}</p>

    <div class="ch-handoff-list">
      {#if !isAssign}
        <button type="button" class="ch-handoff-row ch-handoff-pile" onclick={() => choose(null)}>
          <span class="ch-handoff-pile-icon" aria-hidden="true">↩</span>
          <span class="ch-handoff-name">Leave for anyone</span>
          <span class="ch-handoff-hint">No one gets nagged</span>
        </button>
      {/if}

      {#each pickable as member (member.userId)}
        <button type="button" class="ch-handoff-row" onclick={() => choose(member.userId)}>
          <MemberAvatar
            name={member.displayName}
            initials={member.initials}
            pictureUrl={member.pictureUrl}
            size={28}
            relation={isAssign ? 'Assign to' : 'Hand off to'}
          />
          <span class="ch-handoff-name">{member.displayName}</span>
        </button>
      {/each}
    </div>

    <div class="ch-dialog-actions">
      <button type="button" class="ch-btn-ghost" onclick={onClose}>Cancel</button>
    </div>
  </div>
</dialog>

<style>
  .ch-dialog {
    position: fixed;
    inset: 0;
    margin: auto;
    border: none;
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    padding: 0;
    width: min(420px, calc(100vw - 32px));
    max-height: calc(100vh - 32px);
    box-shadow: var(--shadow-4);
  }
  .ch-dialog::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .ch-dialog-body {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 24px;
  }
  .ch-dialog h2 {
    margin: 0;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .ch-dialog-sub {
    margin: 0;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }

  .ch-handoff-list {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin-top: 4px;
  }
  .ch-handoff-row {
    display: flex;
    align-items: center;
    gap: 12px;
    width: 100%;
    text-align: left;
    font: inherit;
    color: inherit;
    background: transparent;
    border: 1px solid var(--color-line);
    border-radius: var(--radius-sm);
    padding: 10px 12px;
    min-height: 48px;
    cursor: pointer;
    transition:
      background-color 0.15s,
      border-color 0.15s;
  }
  .ch-handoff-row:hover {
    background: var(--color-action-hover);
    border-color: var(--color-line-strong);
  }
  .ch-handoff-name {
    font-weight: 500;
    flex: 1;
    min-width: 0;
  }
  .ch-handoff-hint {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .ch-handoff-pile-icon {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 28px;
    height: 28px;
    border-radius: 50%;
    background: var(--color-action-hover);
    color: var(--color-text-muted);
    font-size: 1rem;
  }

  .ch-dialog-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 8px;
  }
  .ch-btn-ghost {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 40px;
    font-weight: 500;
    background: transparent;
    color: var(--color-primary);
  }
  .ch-btn-ghost:hover {
    background: var(--color-action-hover);
  }
</style>
