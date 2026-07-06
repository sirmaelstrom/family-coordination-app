<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Manage-rooms sheet (v1.2 room manager) — the reorder / delete / add surface
  // that builds directly on the shipped RoomEditSheet (rename / icon / photo).
  // Opened from the Rooms-lens dashboard header. Real rooms only; the virtual
  // General group (roomId === null) is shown as a non-editable reference.
  //
  //  • Reorder: drag rows (svelte-dnd-action — the shopping-list island's house
  //    pattern). On drop we POST the new order (reorderRooms) then reloadBoard()
  //    so the dashboard re-sorts authoritatively.
  //  • Delete: two-tap confirm. The server (RoomService.DeleteRoomAsync) moves
  //    the room's chores to General and deletes its photo in one write — the
  //    confirm copy says so, using the room's server-computed choreCount.
  //  • Add: a thin inline create (reuses createRoom + the shared IconPicker), so
  //    room management is consolidated here as well as in the chore sheets.
  //  • Edit (rename / icon / photo): delegated to the existing RoomEditSheet via
  //    onEditRoom — we do NOT duplicate that surface.
  //
  // A room change is not a chore mutation (no xmin / optimistic path); every
  // mutation is call-the-API then boardStore.reloadBoard(), matching RoomEditSheet.
  // ───────────────────────────────────────────────────────────────────────
  import type { RoomGroup } from '../state.svelte';
  import type { RoomRollupDto } from '../types';
  import { createRoom, deleteRoom, reorderRooms } from '../api';
  import { boardStore } from '../state.svelte';
  import { showToast } from '$lib/shared/toast-store.svelte';
  import IconPicker from './IconPicker.svelte';
  import { dndzone, type DndEvent } from 'svelte-dnd-action';

  interface Props {
    open: boolean;
    /** All room groups from the board (incl. the virtual General group). */
    groups: RoomGroup[];
    /** Open the shipped per-room edit sheet (rename / icon / photo). */
    onEditRoom: (room: RoomRollupDto) => void;
    onClose: () => void;
  }

  let { open, groups, onEditRoom, onClose }: Props = $props();

  /** A draggable row — svelte-dnd-action needs a stable `id` per item. */
  interface RoomRow {
    id: number;
    rollup: RoomRollupDto;
  }

  let dialogEl: HTMLDialogElement | null = $state(null);

  // Local working list for the drag zone. Re-synced from `groups` except while a
  // drag is in flight, so a mid-flight board reload can't clobber the ordering.
  let rows = $state<RoomRow[]>([]);
  let dragging = $state(false);

  // The virtual General group (roomless chores) — shown as a non-editable note.
  let general = $derived<RoomRollupDto | null>(
    groups.find((g) => g.rollup.roomId === null)?.rollup ?? null,
  );

  function syncRows(): void {
    rows = groups
      .filter((g) => g.rollup.roomId !== null)
      .map((g) => g.rollup)
      .sort((a, b) => a.sortOrder - b.sortOrder)
      .map((rollup) => ({ id: rollup.roomId as number, rollup }));
  }

  // Re-derive the working list whenever the board changes and we're not dragging
  // (covers reloadBoard() after add / delete / reorder / a delegated edit).
  $effect(() => {
    if (!dragging) syncRows();
  });

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      resetAdd();
      cancelDelete();
      dialogEl.showModal();
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  // ── Reorder ────────────────────────────────────────────────────────────────
  function handleConsider(e: CustomEvent<DndEvent<RoomRow>>): void {
    dragging = true;
    rows = e.detail.items;
  }
  async function handleFinalize(e: CustomEvent<DndEvent<RoomRow>>): Promise<void> {
    rows = e.detail.items;
    const ordered = rows.map((r) => r.id);
    try {
      await reorderRooms(ordered);
      await boardStore.reloadBoard();
    } catch {
      showToast({ message: "Couldn't save the new order — refreshed.", kind: 'info' });
      await boardStore.reloadBoard();
    } finally {
      // Release the drag guard last so the resync lands on the server order.
      dragging = false;
    }
  }

  // ── Delete (two-tap confirm — irreversible at the room level) ────────────────
  let confirmDeleteId = $state<number | null>(null);
  let deletingId = $state<number | null>(null);

  function askDelete(id: number): void {
    confirmDeleteId = id;
  }
  function cancelDelete(): void {
    confirmDeleteId = null;
  }
  async function doDelete(row: RoomRow): Promise<void> {
    if (deletingId !== null) return;
    deletingId = row.id;
    try {
      await deleteRoom(row.id);
      await boardStore.reloadBoard();
      showToast({ message: `“${row.rollup.name}” deleted.`, kind: 'success' });
    } catch {
      showToast({ message: "Couldn't delete the room. Please try again.", kind: 'error' });
    } finally {
      deletingId = null;
      confirmDeleteId = null;
    }
  }

  /**
   * Confirm copy for the server delete behavior. Under multi-room (M:N, Phase 13) a chore can belong to
   * several rooms, so `choreCount` (which fans out per membership) overstates what actually moves to
   * General: only chores whose SOLE room is this one fall to General; chores also in other rooms keep those.
   * Don't cite the fanned-out count as the move count.
   */
  function deleteConfirmText(rollup: RoomRollupDto): string {
    if (rollup.choreCount === 0) return `Delete “${rollup.name}”? It has no chores.`;
    return `Delete “${rollup.name}”? Chores only in this room move to General; chores also in other rooms keep those.`;
  }

  // ── Add (thin inline create — reuses the createRoom path) ────────────────────
  let showAdd = $state(false);
  let newName = $state('');
  let newIcon = $state<string>('🧹');
  let addError = $state<string | null>(null);
  let adding = $state(false);

  function resetAdd(): void {
    showAdd = false;
    newName = '';
    newIcon = '🧹';
    addError = null;
  }
  async function addRoom(): Promise<void> {
    const trimmed = newName.trim();
    if (!trimmed) {
      addError = 'Give the room a name.';
      return;
    }
    if (adding) return;
    addError = null;
    adding = true;
    try {
      await createRoom({ name: trimmed, icon: newIcon });
      await boardStore.reloadBoard();
      resetAdd();
    } catch {
      addError = "Couldn't create the room — try again.";
    } finally {
      adding = false;
    }
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="ch-sheet">
  <div class="ch-sheet-inner">
    <header class="ch-sheet-head">
      <h2>Manage rooms</h2>
      <p class="ch-sheet-subhead">Reorder, rename, set a photo, or remove a room.</p>
    </header>

    <div class="ch-sheet-body">
      {#if rows.length > 0}
        <ul
          class="ch-rm-list"
          use:dndzone={{
            items: rows,
            type: 'rooms',
            flipDurationMs: 150,
            dropTargetStyle: {},
            delayTouchStart: 250,
          }}
          onconsider={handleConsider}
          onfinalize={handleFinalize}
        >
          {#each rows as row (row.id)}
            <li class="ch-rm-row">
              {#if confirmDeleteId === row.id}
                <span class="ch-rm-confirm" role="alert">{deleteConfirmText(row.rollup)}</span>
                <span class="ch-rm-confirm-actions">
                  <button
                    type="button"
                    class="ch-btn-ghost"
                    onclick={cancelDelete}
                    disabled={deletingId === row.id}
                  >
                    Keep
                  </button>
                  <button
                    type="button"
                    class="ch-btn-danger"
                    onclick={() => doDelete(row)}
                    disabled={deletingId === row.id}
                  >
                    {deletingId === row.id ? 'Deleting…' : 'Delete'}
                  </button>
                </span>
              {:else}
                <span class="ch-rm-handle" aria-hidden="true">⠿</span>
                <span class="ch-rm-icon" aria-hidden="true">{row.rollup.icon}</span>
                <span class="ch-rm-name">{row.rollup.name}</span>
                <span class="ch-rm-count">
                  {row.rollup.choreCount}
                  {row.rollup.choreCount === 1 ? 'chore' : 'chores'}
                </span>
                <span class="ch-rm-actions">
                  <button
                    type="button"
                    class="ch-rm-iconbtn"
                    aria-label="Edit {row.rollup.name}"
                    onclick={() => onEditRoom(row.rollup)}
                  >
                    ✏️
                  </button>
                  <button
                    type="button"
                    class="ch-rm-iconbtn ch-rm-iconbtn-danger"
                    aria-label="Delete {row.rollup.name}"
                    onclick={() => askDelete(row.id)}
                  >
                    🗑️
                  </button>
                </span>
              {/if}
            </li>
          {/each}
        </ul>
      {:else}
        <p class="ch-hint">No rooms yet. Add one below, or create a room while adding a chore.</p>
      {/if}

      {#if general}
        <p class="ch-rm-general">
          <span aria-hidden="true">{general.icon}</span>
          General — where roomless chores (and a deleted room's chores) live. Not editable.
        </p>
      {/if}

      {#if showAdd}
        <div class="ch-newroom">
          <IconPicker value={newIcon} onSelect={(i) => (newIcon = i)} label="New room icon" />
          <div class="ch-newroom-row">
            <input
              type="text"
              bind:value={newName}
              autocomplete="off"
              placeholder="Room name (e.g. Garage)"
              aria-label="New room name"
              onkeydown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  addRoom();
                }
              }}
            />
            <button
              type="button"
              class="ch-btn-primary ch-newroom-add"
              onclick={addRoom}
              disabled={adding || !newName.trim()}
            >
              {adding ? 'Adding…' : 'Add'}
            </button>
            <button type="button" class="ch-btn-ghost" onclick={resetAdd} disabled={adding}>
              Cancel
            </button>
          </div>
          {#if addError}
            <p class="ch-sheet-error" role="alert">{addError}</p>
          {/if}
        </div>
      {:else}
        <button
          type="button"
          class="ch-chip ch-chip-add ch-rm-add"
          onclick={() => {
            showAdd = true;
            addError = null;
          }}
        >
          ＋ Add room
        </button>
      {/if}
    </div>

    <footer class="ch-sheet-actions">
      <button type="button" class="ch-btn-primary" onclick={onClose}>Done</button>
    </footer>
  </div>
</dialog>

<style>
  /* ── Sheet shell (parity with EditChoreSheet / RoomEditSheet) ─────────────── */
  .ch-sheet {
    border: none;
    padding: 0;
    background: var(--color-surface);
    color: var(--color-text);
    box-shadow: var(--shadow-4);
    width: min(520px, 100vw);
    max-height: 90vh;
    margin: auto auto 0;
    border-radius: var(--radius-md) var(--radius-md) 0 0;
  }
  @media (min-width: 600px) {
    .ch-sheet {
      margin: auto;
      border-radius: var(--radius-md);
    }
  }
  .ch-sheet::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .ch-sheet-inner {
    display: flex;
    flex-direction: column;
    max-height: 90vh;
  }
  .ch-sheet-head {
    padding: 20px 24px 8px;
  }
  .ch-sheet-head h2 {
    margin: 0;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .ch-sheet-subhead {
    margin: 4px 0 0;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .ch-sheet-body {
    overflow-y: auto;
    padding: 12px 24px 16px;
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  /* ── Room list ────────────────────────────────────────────────────────────── */
  .ch-rm-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .ch-rm-row {
    display: flex;
    align-items: center;
    gap: 10px;
    min-height: 52px;
    padding: 8px 10px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
  }
  .ch-rm-handle {
    flex-shrink: 0;
    color: var(--color-text-muted);
    font-size: 1.125rem;
    line-height: 1;
    cursor: grab;
    padding: 4px;
    -webkit-tap-highlight-color: transparent;
  }
  .ch-rm-icon {
    flex-shrink: 0;
    font-size: 1.25rem;
    line-height: 1;
  }
  .ch-rm-name {
    flex: 1;
    min-width: 0;
    font-weight: 500;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .ch-rm-count {
    flex-shrink: 0;
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .ch-rm-actions {
    flex-shrink: 0;
    display: inline-flex;
    gap: 4px;
  }
  .ch-rm-iconbtn {
    font: inherit;
    font-size: 1rem;
    line-height: 1;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    border-radius: var(--radius-sm);
    min-width: 40px;
    min-height: 40px;
    cursor: pointer;
    -webkit-tap-highlight-color: transparent;
  }
  .ch-rm-iconbtn:hover {
    background: var(--color-action-hover);
  }
  .ch-rm-iconbtn-danger:hover {
    border-color: var(--color-error);
  }

  /* ── Inline delete confirm (two-tap) ──────────────────────────────────────── */
  .ch-rm-confirm {
    flex: 1;
    min-width: 0;
    font-size: 0.8125rem;
    color: var(--color-text);
  }
  .ch-rm-confirm-actions {
    flex-shrink: 0;
    display: inline-flex;
    gap: 6px;
  }

  /* ── General reference + add ──────────────────────────────────────────────── */
  .ch-rm-general {
    margin: 0;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
    padding: 8px 10px;
    border: 1px dashed var(--color-line);
    border-radius: var(--radius-sm);
  }
  .ch-rm-add {
    align-self: flex-start;
  }

  .ch-hint {
    margin: 0;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .ch-sheet-error {
    margin: 0;
    font-size: 0.875rem;
    color: var(--color-error);
  }

  /* ── Inline "new room" (parity with the chore sheets' create form) ────────── */
  .ch-newroom {
    display: flex;
    flex-direction: column;
    gap: 10px;
    padding: 12px;
    border: 1px dashed var(--color-line-strong);
    border-radius: var(--radius-sm);
  }
  .ch-newroom-row {
    display: flex;
    gap: 8px;
    align-items: center;
    flex-wrap: wrap;
  }
  .ch-newroom-row input[type='text'] {
    flex: 1;
    min-width: 140px;
    font: inherit;
    color: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    min-height: 44px;
  }
  .ch-newroom-row input[type='text']:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
    border-color: var(--color-primary);
  }
  .ch-newroom-add {
    min-height: 44px;
  }
  .ch-chip {
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    color: var(--color-text-muted);
    padding: 6px 14px;
    min-height: 40px;
    border-radius: 18px;
    cursor: pointer;
  }
  .ch-chip:hover {
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .ch-chip-add {
    border-style: dashed;
  }

  /* ── Footer actions (parity with the other sheets) ────────────────────────── */
  .ch-sheet-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 16px 24px calc(20px + env(safe-area-inset-bottom, 0px));
    border-top: 1px solid var(--color-line);
  }
  .ch-btn-ghost,
  .ch-btn-primary,
  .ch-btn-danger {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 44px;
    font-weight: 500;
    letter-spacing: 0.02em;
  }
  .ch-btn-ghost {
    background: transparent;
    color: var(--color-primary);
  }
  .ch-btn-ghost:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .ch-btn-primary {
    background: var(--color-primary);
    color: #fff;
    box-shadow: var(--shadow-1);
  }
  .ch-btn-primary:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .ch-btn-danger {
    background: var(--color-error);
    color: #fff;
    box-shadow: var(--shadow-1);
  }
  .ch-btn-danger:hover:not(:disabled) {
    filter: brightness(0.92);
  }
  .ch-btn-ghost:disabled,
  .ch-btn-primary:disabled,
  .ch-btn-danger:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
