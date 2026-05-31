<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Quick-add sheet (WP-11). A bottom sheet from the FAB to create a chore.
  //
  //  • What needs doing  — free text (required)
  //  • How often         — 3 segmented buttons (D4-B; NO monthly-on-day):
  //                          Just once       → OneOff
  //                          Every N days     → Flexible (+ positive intervalDays)
  //                          Specific day(s)  → Fixed   (+ a DaysOfWeek selection)
  //  • Effort            — Quick / Standard / Big job chips (NAMED tiers, P3 —
  //                        never a raw number field)
  //  • Room              — optional chip row defaulting to General
  //  • Who's on it       — optional; default "Leave for anyone" (pile), with copy
  //                        that no one gets nagged
  //  • Photo             — optional; TWO-STEP (council C2): the parent POSTs the
  //                        create JSON first (no file), then uploads the photo to
  //                        the dedicated /photo route. This sheet just hands the
  //                        chosen File up; it NEVER puts a file in the JSON body.
  //
  // ⚠ M5/M6: no `new Date('YYYY-MM-DD')` anywhere. The "Every N days" cadence is
  // a plain positive integer; "Specific day(s)" is a weekday flag set. "Just once"
  // takes an optional due date — the native date input yields a "YYYY-MM-DD" string
  // we pass straight through as anchorDate (no Date construction). Recurring cadences
  // send no anchorDate (the server derives their dueness from cadence + completion).
  // ───────────────────────────────────────────────────────────────────────
  import type { EffortTier, RecurrenceMode, MemberDto, RoomRollupDto } from '../types';
  import type { CreateChoreRequest } from '../api';
  import { createRoom, uploadRoomPhoto } from '../api';
  import { showToast } from '../toasts.svelte';
  import IconPicker from './IconPicker.svelte';

  /** What the parent needs to create the chore + (optionally) attach a photo. */
  export interface QuickAddValue {
    request: CreateChoreRequest;
    photo: File | null;
  }

  interface Props {
    open: boolean;
    submitting: boolean;
    /** Household members for the optional assignee picker. */
    members: MemberDto[];
    /** Room rollups from the board (incl. the virtual General group, roomId null). */
    rooms: RoomRollupDto[];
    onClose: () => void;
    onSubmit: (value: QuickAddValue) => Promise<void> | void;
  }

  let { open, submitting, members, rooms, onClose, onSubmit }: Props = $props();

  // ── Form state ───────────────────────────────────────────────────────────
  type Cadence = 'once' | 'everyN' | 'days';

  let name = $state('');
  /** Optional emoji icon; '' = none. Reuses the shared <IconPicker>. */
  let choreIcon = $state<string>('');
  let cadence = $state<Cadence>('once');
  let intervalDays = $state('3'); // string for input coercion; parsed at submit
  let selectedDays = $state(new Set<string>());
  /** "YYYY-MM-DD" for the "Just once" due date; '' = none. Passed straight as anchorDate. */
  let dueDate = $state('');
  let effort = $state<EffortTier>('Standard');
  /** null ⇒ General (roomless). */
  let roomId = $state<number | null>(null);
  /** null ⇒ "Leave for anyone" (pile). */
  let assigneeUserId = $state<number | null>(null);
  let photo = $state<File | null>(null);
  let localError = $state<string | null>(null);

  // ── Inline "new room" (v1.2 quick win) ─────────────────────────────────────
  // Create a room without leaving the sheet (the full room manager is deferred —
  // see ROADMAP Phase 12). Created rooms merge into the picker chips and are
  // selected immediately; a later board reload carries them in `rooms`.
  let createdRooms = $state<RoomRollupDto[]>([]);
  let showNewRoom = $state(false);
  let newRoomName = $state('');
  let newRoomIcon = $state<string>('🧹');
  let newRoomPhotoFile = $state<File | null>(null);
  let newRoomError = $state<string | null>(null);
  let creatingRoom = $state(false);

  let dialogEl: HTMLDialogElement | null = $state(null);
  let nameInput: HTMLInputElement | null = $state(null);

  const CADENCES: { id: Cadence; label: string }[] = [
    { id: 'once', label: 'Just once' },
    { id: 'everyN', label: 'Every N days' },
    { id: 'days', label: 'Specific day(s)' },
  ];

  const EFFORTS: { id: EffortTier; label: string }[] = [
    { id: 'Quick', label: 'Quick' },
    { id: 'Standard', label: 'Standard' },
    { id: 'BigJob', label: 'Big job' },
  ];

  // camelCase flag names — must match the C# [Flags] ChoreDaysOfWeek enum
  // serialized via JsonStringEnumConverter(CamelCase). Sunday-first.
  const WEEKDAYS: { flag: string; short: string }[] = [
    { flag: 'sunday', short: 'Sun' },
    { flag: 'monday', short: 'Mon' },
    { flag: 'tuesday', short: 'Tue' },
    { flag: 'wednesday', short: 'Wed' },
    { flag: 'thursday', short: 'Thu' },
    { flag: 'friday', short: 'Fri' },
    { flag: 'saturday', short: 'Sat' },
  ];

  // Picker chips = board rooms + any just-created rooms, deduped by id (the board
  // copy wins once a reload carries real counts). General (roomId null) renders
  // separately, so it's excluded here.
  let roomChips = $derived.by(() => {
    const byId = new Map<number, RoomRollupDto>();
    for (const r of createdRooms) if (r.roomId !== null) byId.set(r.roomId, r);
    for (const r of rooms) if (r.roomId !== null) byId.set(r.roomId, r);
    return [...byId.values()].sort((a, b) => a.sortOrder - b.sortOrder);
  });

  async function addNewRoom(): Promise<void> {
    const trimmed = newRoomName.trim();
    if (!trimmed) {
      newRoomError = 'Give the room a name.';
      return;
    }
    if (creatingRoom) return;
    newRoomError = null;
    creatingRoom = true;
    try {
      const created = await createRoom({ name: trimmed, icon: newRoomIcon });
      // Optional cover photo: upload after create (we now have the room id), then
      // carry the returned path on the local rollup. A failed upload still keeps
      // the room — only the photo is skipped (a later board reload reconciles).
      let createdPhotoPath = created.photoPath;
      if (newRoomPhotoFile) {
        try {
          const up = await uploadRoomPhoto(created.id, newRoomPhotoFile);
          createdPhotoPath = up.photoPath;
        } catch {
          showToast({ message: "Room added, but the photo didn't upload.", kind: 'info' });
        }
      }
      createdRooms = [
        ...createdRooms,
        {
          roomId: created.id,
          name: created.name,
          icon: created.icon,
          photoPath: createdPhotoPath,
          sortOrder: created.sortOrder,
          choreCount: 0,
          dueCount: 0,
          status: 'clean',
        },
      ];
      roomId = created.id;
      newRoomName = '';
      newRoomPhotoFile = null;
      showNewRoom = false;
    } catch {
      newRoomError = "Couldn't create the room — try again.";
    } finally {
      creatingRoom = false;
    }
  }

  function reset() {
    name = '';
    choreIcon = '';
    cadence = 'once';
    intervalDays = '3';
    selectedDays = new Set<string>();
    dueDate = '';
    effort = 'Standard';
    roomId = null;
    assigneeUserId = null;
    photo = null;
    localError = null;
    // Reset the inline new-room form UI (keep createdRooms — a later board reload dedups them).
    showNewRoom = false;
    newRoomName = '';
    newRoomPhotoFile = null;
    newRoomError = null;
    creatingRoom = false;
  }

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      reset();
      dialogEl.showModal();
      queueMicrotask(() => nameInput?.focus());
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  function toggleDay(flag: string) {
    const next = new Set(selectedDays);
    if (next.has(flag)) next.delete(flag);
    else next.add(flag);
    selectedDays = next;
  }

  function onPhotoChange(e: Event) {
    const input = e.currentTarget as HTMLInputElement;
    photo = input.files && input.files.length > 0 ? input.files[0] : null;
  }

  /** Map the 3-button cadence into the API recurrence fields (D4-B). */
  function buildRecurrence():
    | { ok: true; mode: RecurrenceMode; intervalDays: number | null; daysOfWeek: string | null }
    | { ok: false; message: string } {
    switch (cadence) {
      case 'once':
        return { ok: true, mode: 'OneOff', intervalDays: null, daysOfWeek: null };
      case 'everyN': {
        const n = Number(intervalDays);
        if (!Number.isInteger(n) || n < 1) {
          return { ok: false, message: 'Enter how many days between this chore (1 or more).' };
        }
        // Flexible: recurring, decays from the last completion over N days.
        return { ok: true, mode: 'Flexible', intervalDays: n, daysOfWeek: null };
      }
      case 'days': {
        if (selectedDays.size === 0) {
          return { ok: false, message: 'Pick at least one day of the week.' };
        }
        // Fixed weekly-on-weekday. CSV of camelCase flag names.
        const csv = WEEKDAYS.filter((d) => selectedDays.has(d.flag))
          .map((d) => d.flag)
          .join(', ');
        return { ok: true, mode: 'Fixed', intervalDays: null, daysOfWeek: csv };
      }
    }
  }

  async function handleSubmit(e: SubmitEvent) {
    e.preventDefault();
    if (submitting) return;
    const trimmed = name.trim();
    if (!trimmed) {
      localError = 'Give the chore a name.';
      return;
    }
    const recurrence = buildRecurrence();
    if (!recurrence.ok) {
      localError = recurrence.message;
      return;
    }
    localError = null;

    const request: CreateChoreRequest = {
      name: trimmed,
      description: null,
      roomId,
      recurrenceMode: recurrence.mode,
      intervalDays: recurrence.intervalDays,
      anchorDate: cadence === 'once' ? dueDate || null : null,
      daysOfWeek: recurrence.daysOfWeek,
      dayOfMonth: null,
      effortTier: effort,
      icon: choreIcon,
      ownerUserId: null,
      assigneeUserId,
      // Photo is NEVER in the create JSON (council C2) — the parent uploads it
      // separately to /api/chores/{id}/photo after the create returns an id.
      photoPath: null,
    };

    await onSubmit({ request, photo });
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="ch-sheet">
  <form method="dialog" onsubmit={handleSubmit}>
    <header class="ch-sheet-head">
      <h2>Add a chore</h2>
    </header>

    <div class="ch-sheet-body">
      <label class="ch-field">
        <span class="ch-field-label">What needs doing?</span>
        <input
          bind:this={nameInput}
          type="text"
          bind:value={name}
          required
          autocomplete="off"
          placeholder="e.g. Wipe down the counters"
        />
      </label>

      <fieldset class="ch-field">
        <legend class="ch-field-label">Icon (optional)</legend>
        <div class="ch-icon-row">
          <button
            type="button"
            class="ch-chip"
            class:active={choreIcon === ''}
            aria-pressed={choreIcon === ''}
            onclick={() => (choreIcon = '')}
          >
            No icon
          </button>
          <IconPicker value={choreIcon || null} onSelect={(i) => (choreIcon = i)} label="Chore icon" />
        </div>
      </fieldset>

      <fieldset class="ch-field">
        <legend class="ch-field-label">How often?</legend>
        <div class="ch-segmented" role="group" aria-label="How often">
          {#each CADENCES as c (c.id)}
            <button
              type="button"
              class="ch-seg"
              class:active={cadence === c.id}
              aria-pressed={cadence === c.id}
              onclick={() => (cadence = c.id)}
            >
              {c.label}
            </button>
          {/each}
        </div>

        {#if cadence === 'once'}
          <label class="ch-subfield">
            <span class="ch-subfield-label">Due date (optional)</span>
            <input type="date" bind:value={dueDate} aria-label="Due date" />
          </label>
        {:else if cadence === 'everyN'}
          <label class="ch-subfield">
            <span class="ch-subfield-label">Every</span>
            <input
              type="text"
              inputmode="numeric"
              pattern="[0-9]*"
              bind:value={intervalDays}
              class="ch-interval-input"
              aria-label="Number of days"
            />
            <span class="ch-subfield-label">days</span>
          </label>
        {:else if cadence === 'days'}
          <div class="ch-weekdays" role="group" aria-label="Days of the week">
            {#each WEEKDAYS as day (day.flag)}
              <button
                type="button"
                class="ch-day"
                class:active={selectedDays.has(day.flag)}
                aria-pressed={selectedDays.has(day.flag)}
                onclick={() => toggleDay(day.flag)}
              >
                {day.short}
              </button>
            {/each}
          </div>
        {/if}
      </fieldset>

      <fieldset class="ch-field">
        <legend class="ch-field-label">Effort</legend>
        <div class="ch-chip-row" role="group" aria-label="Effort">
          {#each EFFORTS as e (e.id)}
            <button
              type="button"
              class="ch-chip"
              class:active={effort === e.id}
              aria-pressed={effort === e.id}
              onclick={() => (effort = e.id)}
            >
              {e.label}
            </button>
          {/each}
        </div>
      </fieldset>

      <fieldset class="ch-field">
        <legend class="ch-field-label">Room</legend>
        <div class="ch-chip-row" role="group" aria-label="Room">
          <button
            type="button"
            class="ch-chip"
            class:active={roomId === null}
            aria-pressed={roomId === null}
            onclick={() => (roomId = null)}
          >
            🏠 General
          </button>
          {#each roomChips as room (room.roomId ?? 'general')}
            {#if room.roomId !== null}
              <button
                type="button"
                class="ch-chip"
                class:active={roomId === room.roomId}
                aria-pressed={roomId === room.roomId}
                onclick={() => (roomId = room.roomId)}
              >
                {room.icon} {room.name}
              </button>
            {/if}
          {/each}
          {#if !showNewRoom}
            <button
              type="button"
              class="ch-chip ch-chip-add"
              onclick={() => {
                showNewRoom = true;
                newRoomError = null;
              }}
            >
              ＋ New room
            </button>
          {/if}
        </div>

        {#if showNewRoom}
          <div class="ch-newroom">
            <IconPicker value={newRoomIcon} onSelect={(i) => (newRoomIcon = i)} label="New room icon" />
            <div class="ch-newroom-row">
              <input
                type="text"
                bind:value={newRoomName}
                autocomplete="off"
                placeholder="Room name (e.g. Garage)"
                aria-label="New room name"
                onkeydown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    addNewRoom();
                  }
                }}
              />
              <button
                type="button"
                class="ch-btn-primary ch-newroom-add"
                onclick={addNewRoom}
                disabled={creatingRoom || !newRoomName.trim()}
              >
                {creatingRoom ? 'Adding…' : 'Add'}
              </button>
              <button
                type="button"
                class="ch-btn-ghost"
                onclick={() => {
                  showNewRoom = false;
                  newRoomName = '';
                  newRoomPhotoFile = null;
                  newRoomError = null;
                }}
                disabled={creatingRoom}
              >
                Cancel
              </button>
            </div>
            <label class="ch-newroom-photo">
              <span class="ch-hint">Cover photo (optional)</span>
              <input
                type="file"
                accept="image/*"
                onchange={(e) => {
                  const input = e.currentTarget as HTMLInputElement;
                  newRoomPhotoFile = input.files && input.files.length > 0 ? input.files[0] : null;
                }}
              />
              {#if newRoomPhotoFile}
                <span class="ch-hint">{newRoomPhotoFile.name}</span>
              {/if}
            </label>
            {#if newRoomError}
              <p class="ch-sheet-error" role="alert">{newRoomError}</p>
            {/if}
          </div>
        {/if}
      </fieldset>

      <fieldset class="ch-field">
        <legend class="ch-field-label">Who's on it?</legend>
        <div class="ch-chip-row" role="group" aria-label="Assignee">
          <button
            type="button"
            class="ch-chip"
            class:active={assigneeUserId === null}
            aria-pressed={assigneeUserId === null}
            onclick={() => (assigneeUserId = null)}
          >
            Leave for anyone
          </button>
          {#each members as member (member.userId)}
            <button
              type="button"
              class="ch-chip"
              class:active={assigneeUserId === member.userId}
              aria-pressed={assigneeUserId === member.userId}
              onclick={() => (assigneeUserId = member.userId)}
            >
              {member.displayName}
            </button>
          {/each}
        </div>
        {#if assigneeUserId === null}
          <p class="ch-hint">Anyone can pick it up — no one gets nagged.</p>
        {/if}
      </fieldset>

      <label class="ch-field">
        <span class="ch-field-label">Photo (optional)</span>
        <input type="file" accept="image/*" onchange={onPhotoChange} />
        {#if photo}
          <span class="ch-hint">{photo.name}</span>
        {/if}
      </label>

      {#if localError}
        <p class="ch-sheet-error" role="alert">{localError}</p>
      {/if}
    </div>

    <footer class="ch-sheet-actions">
      <button type="button" class="ch-btn-ghost" onclick={onClose} disabled={submitting}>
        Cancel
      </button>
      <button type="submit" class="ch-btn-primary" disabled={submitting || !name.trim()}>
        {submitting ? 'Adding…' : 'Add chore'}
      </button>
    </footer>
  </form>
</dialog>

<style>
  .ch-sheet {
    border: none;
    padding: 0;
    background: var(--color-surface);
    color: var(--color-text);
    box-shadow: var(--shadow-4);
    width: min(520px, 100vw);
    max-height: 90vh;
    /* Bottom sheet on mobile; centered card on wide screens. */
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
  .ch-sheet form {
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
  .ch-sheet-body {
    overflow-y: auto;
    padding: 8px 24px 16px;
    display: flex;
    flex-direction: column;
    gap: 18px;
  }

  .ch-field {
    border: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .ch-field-label {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text-muted);
    padding: 0;
  }
  input[type='text'] {
    font: inherit;
    color: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    min-height: 44px;
  }
  input[type='text']:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
    border-color: var(--color-primary);
  }
  input[type='file'] {
    font: inherit;
    color: var(--color-text-muted);
    font-size: 0.875rem;
  }
  input[type='date'] {
    font: inherit;
    color: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    min-height: 44px;
  }

  .ch-segmented {
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
  }
  .ch-seg {
    flex: 1;
    min-width: 96px;
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    color: var(--color-text-muted);
    padding: 8px 10px;
    min-height: 40px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    transition:
      background-color 0.15s,
      color 0.15s,
      border-color 0.15s;
  }
  .ch-seg.active {
    background: var(--color-primary);
    border-color: var(--color-primary);
    color: #fff;
  }
  .ch-seg:hover:not(.active) {
    background: var(--color-action-hover);
    color: var(--color-text);
  }

  .ch-subfield {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .ch-subfield-label {
    color: var(--color-text-muted);
  }
  .ch-interval-input {
    width: 72px;
    text-align: center;
  }

  .ch-weekdays {
    display: flex;
    gap: 4px;
    flex-wrap: wrap;
  }
  .ch-day {
    font: inherit;
    font-size: 0.75rem;
    font-weight: 500;
    width: 44px;
    min-height: 40px;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    color: var(--color-text-muted);
    border-radius: var(--radius-sm);
    cursor: pointer;
    transition:
      background-color 0.15s,
      color 0.15s,
      border-color 0.15s;
  }
  .ch-day.active {
    background: var(--color-primary);
    border-color: var(--color-primary);
    color: #fff;
  }
  .ch-day:hover:not(.active) {
    background: var(--color-action-hover);
    color: var(--color-text);
  }

  .ch-chip-row {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
  }
  /* Icon field: the "No icon" chip sits inline with the emoji palette. */
  .ch-icon-row {
    display: flex;
    gap: 8px;
    align-items: center;
    flex-wrap: wrap;
  }
  .ch-chip {
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    color: var(--color-text-muted);
    padding: 6px 14px;
    min-height: 36px;
    border-radius: 18px;
    cursor: pointer;
    transition:
      background-color 0.15s,
      color 0.15s,
      border-color 0.15s;
  }
  .ch-chip.active {
    background: var(--color-primary);
    border-color: var(--color-primary);
    color: #fff;
  }
  .ch-chip:hover:not(.active) {
    background: var(--color-action-hover);
    color: var(--color-text);
  }

  /* Inline "new room" (v1.2) */
  .ch-chip-add {
    border-style: dashed;
  }
  .ch-newroom {
    display: flex;
    flex-direction: column;
    gap: 10px;
    margin-top: 10px;
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
  }
  .ch-newroom-add {
    min-height: 44px;
  }
  .ch-newroom-photo {
    display: flex;
    flex-direction: column;
    gap: 6px;
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

  .ch-sheet-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 16px 24px calc(20px + env(safe-area-inset-bottom, 0px));
    border-top: 1px solid var(--color-line);
  }
  .ch-btn-ghost,
  .ch-btn-primary {
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
  .ch-btn-primary:disabled,
  .ch-btn-ghost:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
