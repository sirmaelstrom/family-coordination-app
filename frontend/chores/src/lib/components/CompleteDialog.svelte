<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Complete dialog (WP-07, D7). Shown ONLY for multi-person (co-sign) chores
  // — `requiredCount > 1`. Mirrors the HandOffPicker modal pattern: a styled
  // <dialog> hosted in App.svelte, opened via the card's `onComplete` callback.
  //
  // It lets the present member record who marked the chore done — themselves
  // (prefilled) plus, optionally, another household member who's also present
  // (the escape hatch that satisfies the count in one action). Members already
  // in `contributorUserIds` have signed THIS occurrence already; they're shown
  // checked + disabled (D6 distinctness — the server dedupes regardless).
  //
  // v1 is participant selection ONLY — there is no completion note/photo UI in
  // the app today, so adding one is out of scope (WP-07 boundary).
  //
  // SERVER fields only (MN3): `completedCount`/`requiredCount`/`contributorUserIds`
  // come straight off the chore DTO; no client count/membership math.
  // ───────────────────────────────────────────────────────────────────────
  import type { ChoreDto, MemberDto } from '../types';
  import { memberFor } from '../state.svelte';
  import MemberAvatar from './MemberAvatar.svelte';

  interface Props {
    open: boolean;
    /** The target co-sign chore (null while closed). Drives the heading + progress. */
    chore: ChoreDto | null;
    members: MemberDto[];
    /** The viewing user's id — prefilled as a participant. */
    currentUserId: number;
    onClose: () => void;
    /** The chosen NEW participant ids (current user + any newly selected; never the already-in). */
    onSubmit: (participantUserIds: number[]) => void;
  }

  let { open, chore, members, currentUserId, onClose, onSubmit }: Props = $props();

  let dialogEl: HTMLDialogElement | null = $state(null);

  // The contributors who have already signed THIS occurrence (server field).
  let alreadyIn = $derived<number[]>(chore?.contributorUserIds ?? []);

  // The members the user can newly select (everyone NOT already in). The
  // current user, if not already in, is prefilled (see `selected` seeding).
  let selectable = $derived(members.filter((m) => !alreadyIn.includes(m.userId)));

  // Newly-selected participant ids (excludes the already-in contributors). A
  // Set swapped by reference so the `$state` reactivity fires.
  let selected = $state(new Set<number>());

  // (Re)seed the selection whenever the dialog opens for a chore: prefill the
  // current user if they haven't already signed. Tracking `open`/`chore` makes
  // this re-run on each open (and on a target change).
  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      const next = new Set<number>();
      if (chore && !(chore.contributorUserIds ?? []).includes(currentUserId)) {
        next.add(currentUserId);
      }
      selected = next;
      dialogEl.showModal();
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  function toggle(userId: number): void {
    const next = new Set(selected);
    if (next.has(userId)) next.delete(userId);
    else next.add(userId);
    selected = next;
  }

  function nameOf(userId: number): string {
    if (userId === currentUserId) return 'You';
    return memberFor(userId)?.displayName ?? 'Someone';
  }

  // At least the current user (or one newly-selected member) is required to
  // submit. If the user is already in, they must pick someone new.
  let canSubmit = $derived(selected.size > 0);

  function submit(): void {
    if (!canSubmit) return;
    // The server dedupes against existing contributors, but we send only the
    // NEW ids ("who's signing now") — never re-send the already-in.
    onSubmit([...selected]);
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="ch-dialog">
  {#if open && chore}
    <div class="ch-dialog-body">
      <h2>Mark done</h2>
      <p class="ch-dialog-sub">
        “{chore.name}” needs {chore.requiredCount} people — {chore.completedCount} of {chore.requiredCount}
        done. Who's marking it done?
      </p>

      {#if alreadyIn.length > 0}
        <div class="ch-cd-already">
          <span class="ch-cd-already-label">Already in</span>
          <div class="ch-cd-already-list">
            {#each alreadyIn as uid (uid)}
              {@const m = memberFor(uid)}
              <span class="ch-cd-already-chip" title="{nameOf(uid)} has marked this done">
                <MemberAvatar
                  name={m?.displayName ?? null}
                  initials={m?.initials ?? null}
                  pictureUrl={m?.pictureUrl ?? null}
                  size={22}
                  relation="Marked done by"
                />
                {nameOf(uid)}
              </span>
            {/each}
          </div>
        </div>
      {/if}

      <div class="ch-cd-list" role="group" aria-label="Who's marking this done">
        {#if selectable.length === 0}
          <p class="ch-cd-empty">Everyone has already marked this done.</p>
        {:else}
          {#each selectable as member (member.userId)}
            <label class="ch-cd-row" class:checked={selected.has(member.userId)}>
              <input
                type="checkbox"
                checked={selected.has(member.userId)}
                onchange={() => toggle(member.userId)}
              />
              <MemberAvatar
                name={member.displayName}
                initials={member.initials}
                pictureUrl={member.pictureUrl}
                size={28}
                relation="Mark done for"
              />
              <span class="ch-cd-name">{nameOf(member.userId)}</span>
            </label>
          {/each}
        {/if}
      </div>

      <div class="ch-dialog-actions">
        <button type="button" class="ch-btn-ghost" onclick={onClose}>Cancel</button>
        <button type="button" class="ch-btn-solid" onclick={submit} disabled={!canSubmit}>
          Mark done
        </button>
      </div>
    </div>
  {/if}
</dialog>

<style>
  /* Gate the layout to [open] so the CLOSED dialog never paints a full-viewport
     overlay that eats clicks (project memory: dialog-display-css-click-trap). */
  .ch-dialog[open] {
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

  /* ── Already-signed contributors ──────────────────────────────────────── */
  .ch-cd-already {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .ch-cd-already-label {
    font-size: 0.6875rem;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--color-text-muted);
  }
  .ch-cd-already-list {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }
  .ch-cd-already-chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 0.8125rem;
    color: var(--color-text);
    background: var(--color-action-hover);
    border-radius: var(--radius-sm);
    padding: 4px 10px 4px 4px;
  }

  /* ── Selectable participants ──────────────────────────────────────────── */
  .ch-cd-list {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin-top: 4px;
  }
  .ch-cd-row {
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
  .ch-cd-row:hover {
    background: var(--color-action-hover);
    border-color: var(--color-line-strong);
  }
  .ch-cd-row.checked {
    border-color: var(--color-primary);
    background: var(--color-action-hover);
  }
  .ch-cd-row input[type='checkbox'] {
    width: 18px;
    height: 18px;
    accent-color: var(--color-primary);
    cursor: pointer;
  }
  .ch-cd-name {
    font-weight: 500;
    flex: 1;
    min-width: 0;
  }
  .ch-cd-empty {
    margin: 0;
    font-size: 0.875rem;
    color: var(--color-text-muted);
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
  .ch-btn-solid {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 40px;
    font-weight: 500;
    background: var(--color-primary);
    color: #fff;
  }
  .ch-btn-solid:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .ch-btn-solid:disabled {
    opacity: 0.55;
    cursor: not-allowed;
  }
</style>
