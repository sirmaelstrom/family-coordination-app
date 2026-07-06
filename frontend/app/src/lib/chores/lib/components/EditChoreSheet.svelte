<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Edit-chore sheet (WP-10). A bottom sheet pre-filled from an existing
  // ChoreDto that drives PUT /api/chores/{id} via the store.
  //
  //  • Same 3-segment cadence as QuickAddSheet (D4-B; NO monthly-on-day):
  //      Just once       → OneOff
  //      Every N days    → Flexible (+ positive intervalDays)
  //      Specific day(s) → Fixed   (+ a DaysOfWeek selection)
  //  • Assignment is NOT editable (v1.0 D6 — moves via claim/handoff).
  //  • Edit-photo (council C4): if the user picks a new file, we upload it
  //    first (POST /photo → { photoPath }) then include the returned path in
  //    the PUT body. To keep the existing photo, send the current photoPath
  //    unchanged. No file in the JSON body (same two-step as QuickAdd C2).
  //
  // ⚠ MN9: no `new Date('YYYY-MM-DD')` anywhere. intervalDays is a plain
  //   positive integer; daysOfWeek is a weekday CSV; anchorDate (one-off due
  //   date) is the date input's "YYYY-MM-DD" string passed straight through.
  // ───────────────────────────────────────────────────────────────────────
  import type { EffortTier, RecurrenceMode, MemberDto, RoomRollupDto, ChoreDto } from '../types';
  import type { UpdateChoreRequest } from '../api';
  import { boardStore } from '../state.svelte';
  import { uploadChorePhoto, createRoom, uploadRoomPhoto } from '../api';
  import { showToast } from '$lib/shared/toast-store.svelte';
  import { minFloorDate } from '../dates';
  import IconPicker from './IconPicker.svelte';

  interface Props {
    open: boolean;
    /** The chore being edited — used to pre-fill the form. */
    chore: ChoreDto | null;
    /** Household members for the optional owner picker. */
    members: MemberDto[];
    /** Room rollups from the board. */
    rooms: RoomRollupDto[];
    onClose: () => void;
  }

  let { open, chore, members, rooms, onClose }: Props = $props();

  type Cadence = 'once' | 'everyN' | 'days';

  // ── Form state ────────────────────────────────────────────────────────────
  let name = $state('');
  let description = $state('');
  /** Optional emoji icon; '' = none. Reuses the shared <IconPicker>. */
  let choreIcon = $state<string>('');
  let cadence = $state<Cadence>('once');
  let intervalDays = $state('3');
  let selectedDays = $state(new Set<string>());
  /** "YYYY-MM-DD" for the "Just once" due date; '' = none. Passed straight as anchorDate. */
  let dueDate = $state('');
  /**
   * "YYYY-MM-DD" next-due floor (snooze) for a RECURRING chore; '' = no floor. Pre-filled from the chore's
   * snoozedUntil; sent as `snoozedUntil` in the PUT (MN4 — passed straight through, no Date construction). Only
   * surfaced for recurring cadences; for a OneOff the due-date field is the reschedule lever instead.
   */
  let nextDueDate = $state('');
  let effort = $state<EffortTier>('Standard');
  let roomId = $state<number | null>(null);
  let ownerUserId = $state<number | null>(null);
  /**
   * Single-person assignee (null = up for grabs). Assignment is NOT part of the
   * PUT contract — it's applied via the dedicated hand-off endpoint after the
   * metadata save (see handleSubmit). Only meaningful when requiredCount === 1.
   */
  let assignTo = $state<number | null>(null);
  /**
   * Multi-person roster selection (when requiredCount > 1). Pre-filled from the
   * chore's current roster; reconciled via the roster assign/leave endpoints on
   * save. Ignored when requiredCount === 1 (that path uses `assignTo`).
   */
  let assignedUserIds = $state<number[]>([]);
  /** 1 = single person (default); ≥2 = multi-person requirement. */
  let requiredCount = $state(1);
  let existingPhotoPath = $state<string | null>(null);
  let newPhotoFile = $state<File | null>(null);
  let localError = $state<string | null>(null);
  let submitting = $state(false);
  // Delete is a two-tap confirm (irreversible). The store's optimistic remove() handles
  // rollback + reconcile + error toast; we just close the sheet after firing it.
  let confirmingDelete = $state(false);
  let deleting = $state(false);

  // ── Inline "new room" (v1.2 quick win) ─────────────────────────────────────
  // Create a room without leaving the sheet (the full room manager is deferred —
  // see ROADMAP Phase 12). Created rooms are merged into the picker chips below
  // and selected immediately; a later board reload carries them in `rooms`.
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

  /**
   * Parse the DTO's camelCase weekday CSV (e.g. "monday, thursday") back into the
   * weekday flag Set the buttons bind to. Tolerant of spacing/casing so it matches
   * the WEEKDAYS flags regardless of how the server enum serializes.
   */
  function parseDaysOfWeek(csv: string | null): Set<string> {
    if (!csv) return new Set<string>();
    return new Set(
      csv
        .split(',')
        .map((s) => s.trim().toLowerCase())
        .filter(Boolean),
    );
  }

  /** Derive the cadence + sub-values from the ChoreDto when pre-filling. */
  function choreToFormState(c: ChoreDto): void {
    name = c.name;
    description = c.description ?? '';
    choreIcon = c.icon ?? '';
    // Phase 13: prefill the single-select transient from the first membership (WP-06 makes it multi-select).
    roomId = c.roomIds[0] ?? null;
    effort = c.effortTier;
    ownerUserId = c.ownerUserId ?? null;
    // Single-person assignment, pre-filled from the chore's CURRENT holder
    // (assigned or claimed); a None/pile chore reads as "Up for grabs" (null).
    assignTo = c.assignmentKind !== 'none' ? (c.assigneeUserId ?? null) : null;
    // Multi-person roster pre-fill — everyone currently on the roster (the board
    // payload carries the authoritative roster for X>1 chores).
    assignedUserIds = c.roster.map((m) => m.userId);
    requiredCount = c.requiredCount ?? 1;
    existingPhotoPath = c.photoPath;
    newPhotoFile = null;
    localError = null;
    confirmingDelete = false;
    deleting = false;
    // One-off due date (anchorDate "YYYY-MM-DD"). Pre-filled unconditionally so it
    // survives toggling cadence away from and back to "Just once" within an edit.
    dueDate = c.anchorDate ?? '';
    // Next-due floor (snooze) for a recurring chore — pre-filled ONLY when the floor is ACTIVE
    // (server-computed `isSnoozed` = today < snoozedUntil; NO client date math). An expired floor reads
    // blank so an unrelated edit never re-sends a past date — which the server now rejects (ValidateFloor).
    nextDueDate = c.isSnoozed ? (c.snoozedUntil ?? '') : '';

    // Map recurrenceMode → cadence (D4-B; no monthly-on-day). The DTO now echoes
    // intervalDays + daysOfWeek (camelCase CSV) + anchorDate so we pre-fill the
    // sub-values too — fixes editing a fixed-weekly / every-N / dated one-off chore
    // losing the existing selection.
    switch (c.recurrenceMode) {
      case 'OneOff':
        cadence = 'once';
        intervalDays = '3';
        selectedDays = new Set<string>();
        break;
      case 'Flexible':
        cadence = 'everyN';
        intervalDays = c.intervalDays != null ? String(c.intervalDays) : '3';
        selectedDays = new Set<string>();
        break;
      case 'Fixed':
        cadence = 'days';
        intervalDays = '3';
        selectedDays = parseDaysOfWeek(c.daysOfWeek);
        break;
    }
  }

  $effect(() => {
    if (!dialogEl) return;
    if (open && chore && !dialogEl.open) {
      choreToFormState(chore);
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

  /** Toggle a member in the multi-person roster selection (requiredCount > 1). */
  function toggleAssignee(userId: number) {
    assignedUserIds = assignedUserIds.includes(userId)
      ? assignedUserIds.filter((id) => id !== userId)
      : [...assignedUserIds, userId];
  }

  function onPhotoChange(e: Event) {
    const input = e.currentTarget as HTMLInputElement;
    newPhotoFile = input.files && input.files.length > 0 ? input.files[0] : null;
  }

  // ── Checklist (optional) — immediate / versionless store ops (Phase 14) ────
  // These run independently of the sheet's PUT-on-Save flow: each calls the
  // versionless store path (boardStore.{add,rename,remove}Subtask) at once. The
  // sheet renders the chore's LIVE subtasks straight off the (deep-reactive)
  // board chore, so an add/remove reflects without a local mirror. Checking
  // items is from the card; the sheet manages the list. Never gates completion.
  let newSubtaskTitle = $state('');

  // Render the checklist from the LIVE board chore (looked up by id), NOT the
  // `chore` prop. App captures that prop reference when the sheet opens, so a
  // liveness refetch (~20s) swaps the board proxy underneath and the prop goes
  // stale — an added item lands on the live proxy but the sheet kept rendering
  // the old one (it only showed after save/close/reopen). The id-based lookup
  // always tracks the current proxy, so add/remove/rename reflect immediately.
  let liveSubtasks = $derived(
    (chore ? boardStore.choresById.get(chore.id)?.subtasks : null) ?? chore?.subtasks ?? [],
  );

  function addSubtaskItem(): void {
    if (!chore) return;
    const title = newSubtaskTitle.trim();
    if (!title) return;
    boardStore.addSubtask(chore.id, title);
    newSubtaskTitle = '';
  }

  /** Rename on blur/Enter only when the value actually changed (store ignores blanks/no-ops). */
  function renameSubtaskItem(subtaskId: number, value: string): void {
    if (!chore) return;
    boardStore.renameSubtask(chore.id, subtaskId, value);
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
        return { ok: true, mode: 'Flexible', intervalDays: n, daysOfWeek: null };
      }
      case 'days': {
        if (selectedDays.size === 0) {
          return { ok: false, message: 'Pick at least one day of the week.' };
        }
        const csv = WEEKDAYS.filter((d) => selectedDays.has(d.flag))
          .map((d) => d.flag)
          .join(', ');
        return { ok: true, mode: 'Fixed', intervalDays: null, daysOfWeek: csv };
      }
    }
  }

  async function handleSubmit(e: SubmitEvent) {
    e.preventDefault();
    if (submitting || !chore) return;
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
    submitting = true;

    try {
      // Edit-photo flow (council C4): if the user picked a new file, upload it
      // first to get a photoPath, then include it in the PUT body.
      // To keep the existing photo, send the current photoPath unchanged.
      let resolvedPhotoPath: string | null = existingPhotoPath;
      if (newPhotoFile) {
        try {
          const uploadResult = await uploadChorePhoto(chore.id, newPhotoFile);
          resolvedPhotoPath = uploadResult.photoPath;
        } catch {
          // The edit can still proceed without a photo.
          showToast({ message: "Photo upload failed — saving other changes.", kind: 'info' });
          resolvedPhotoPath = existingPhotoPath;
        }
      }

      const body: UpdateChoreRequest = {
        name: trimmed,
        description: description.trim() || null,
        // Phase 13: send the membership set. WP-05 keeps a single-select transient (roomId); WP-06 makes it
        // true multi-select. Empty selection clears to General.
        roomIds: roomId == null ? [] : [roomId],
        recurrenceMode: recurrence.mode,
        intervalDays: recurrence.intervalDays,
        // One-off due date: pass the date input's "YYYY-MM-DD" straight through as
        // anchorDate (no Date construction — MN9). Recurring cadences send null; the
        // server manages their anchor dates.
        anchorDate: cadence === 'once' ? dueDate || null : null,
        daysOfWeek: recurrence.daysOfWeek,
        dayOfMonth: null,
        effortTier: effort,
        icon: choreIcon,
        ownerUserId,
        requiredCount,
        version: chore.version,
        photoPath: resolvedPhotoPath,
        // Next-due floor (snooze). For a recurring chore it's the "Next due date" field above (blank ⇒ clear).
        // For a OneOff there is no floor field — the "Due date" (anchorDate) IS the reschedule lever (sheet
        // header note): so when the user CHANGES the due date, clear any existing floor so the new date is
        // authoritative (the calculator uses SnoozedUntil ?? AnchorDate — a stale floor would otherwise silently
        // override the edit). When the due date is untouched, preserve the floor so an unrelated edit (e.g. a
        // rename) never drops a card-set snooze. Plain string compare of the date inputs — no Date math (MN4).
        snoozedUntil:
          recurrence.mode === 'OneOff'
            ? (dueDate !== (chore.anchorDate ?? '') ? null : (chore.snoozedUntil ?? null))
            : nextDueDate || null,
      };

      // Snapshot the roster BEFORE the save so the multi-person reconcile diffs
      // against the chore's pre-edit roster (the PUT response carries an empty one).
      const originalRoster = chore.roster.map((m) => ({ userId: m.userId, state: m.state }));

      await boardStore.edit(chore.id, body);

      // Assignment isn't part of the PUT contract — it moves via the dedicated
      // claim/hand-off + roster endpoints. Apply it AFTER the metadata save:
      // boardStore.edit just refreshed this chore's version in the board, so the
      // follow-on read carries a fresh xmin (no self-409).
      if (requiredCount === 1) {
        // Single-person: a member target lands a deliberate Assigned via hand-off;
        // clearing back to "Up for grabs" returns a held chore to the pile.
        const currentAssignee =
          chore.assignmentKind !== 'none' ? (chore.assigneeUserId ?? null) : null;
        if (assignTo !== currentAssignee) {
          if (assignTo === null) {
            if (currentAssignee !== null) await boardStore.handOff(chore.id, null);
          } else {
            await boardStore.handOff(chore.id, assignTo);
          }
        }
      } else {
        // Multi-person: reconcile the named roster to the chosen set (adds/removes
        // via the roster endpoints, diffed against the pre-edit roster).
        await boardStore.applyRosterSelection(chore.id, originalRoster, assignedUserIds);
      }

      onClose();
    } finally {
      submitting = false;
    }
  }

  async function handleDelete() {
    if (!chore || deleting) return;
    deleting = true;
    try {
      // Optimistic removal + rollback/reconcile + error toast all live in the store.
      await boardStore.remove(chore.id);
      onClose();
    } finally {
      deleting = false;
    }
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="ch-sheet">
  <form method="dialog" onsubmit={handleSubmit}>
    <header class="ch-sheet-head">
      <h2>Edit chore</h2>
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

      <label class="ch-field">
        <span class="ch-field-label">Description (optional)</span>
        <input
          type="text"
          bind:value={description}
          autocomplete="off"
          placeholder="Any extra detail…"
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

        {#if cadence === 'everyN' || cadence === 'days'}
          <!--
            Next due date (snooze floor). Recurring-only: a OneOff uses its Due date
            above. Sent as snoozedUntil in the PUT; the date input's "YYYY-MM-DD" is
            passed straight through (MN4 — no Date construction). Clearing it un-snoozes.
          -->
          <label class="ch-subfield">
            <span class="ch-subfield-label">Next due date (optional)</span>
            <input type="date" bind:value={nextDueDate} aria-label="Next due date" min={minFloorDate()} />
          </label>
          <p class="ch-hint">
            Setting a next-due date doesn't change the schedule — this chore will still come due on its normal
            recurring day(s) from then on.
          </p>
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

      {#if members.length >= 2}
        <fieldset class="ch-field">
          <legend class="ch-field-label">How many people?</legend>
          <div class="ch-chip-row" role="group" aria-label="How many people">
            <button
              type="button"
              class="ch-chip"
              class:active={requiredCount === 1}
              aria-pressed={requiredCount === 1}
              onclick={() => (requiredCount = 1)}
            >
              One person
            </button>
            <button
              type="button"
              class="ch-chip"
              class:active={requiredCount > 1}
              aria-pressed={requiredCount > 1}
              onclick={() => { if (requiredCount < 2) requiredCount = 2; }}
            >
              Needs more than one
            </button>
          </div>
          {#if requiredCount > 1}
            <div class="ch-chip-row" role="group" aria-label="Number of people needed">
              {#each { length: members.length - 1 } as _, i (i)}
                {@const n = i + 2}
                <button
                  type="button"
                  class="ch-chip"
                  class:active={requiredCount === n}
                  aria-pressed={requiredCount === n}
                  onclick={() => (requiredCount = n)}
                >
                  Needs {n} people
                </button>
              {/each}
            </div>
          {/if}
        </fieldset>
      {/if}

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
        <legend class="ch-field-label">Minder (optional)</legend>
        <div class="ch-chip-row" role="group" aria-label="Minder">
          <button
            type="button"
            class="ch-chip"
            class:active={ownerUserId === null}
            aria-pressed={ownerUserId === null}
            onclick={() => (ownerUserId = null)}
          >
            No minder
          </button>
          {#each members as member (member.userId)}
            <button
              type="button"
              class="ch-chip"
              class:active={ownerUserId === member.userId}
              aria-pressed={ownerUserId === member.userId}
              onclick={() => (ownerUserId = member.userId)}
            >
              {member.displayName}
            </button>
          {/each}
        </div>
      </fieldset>

      <!--
        Assignment is applied through the dedicated assignment endpoints on save
        (handleSubmit), NOT the PUT body — so the assignment trio / roster events
        stay the single source of truth.
          • single-person (requiredCount === 1): "Assign to" one member, via hand-off.
          • multi-person (requiredCount > 1): "Assign people", reconciled against the
            chore's named roster via roster assign/leave.
        The field tracks the LIVE requiredCount, so flipping "How many people?"
        above swaps which picker shows.
      -->
      {#if requiredCount === 1}
        <fieldset class="ch-field">
          <legend class="ch-field-label">Assign to (optional)</legend>
          <div class="ch-chip-row" role="group" aria-label="Assign to">
            <button
              type="button"
              class="ch-chip"
              class:active={assignTo === null}
              aria-pressed={assignTo === null}
              onclick={() => (assignTo = null)}
            >
              Up for grabs
            </button>
            {#each members as member (member.userId)}
              <button
                type="button"
                class="ch-chip"
                class:active={assignTo === member.userId}
                aria-pressed={assignTo === member.userId}
                onclick={() => (assignTo = member.userId)}
              >
                {member.displayName}
              </button>
            {/each}
          </div>
          <p class="ch-hint">Pin this on one person, or leave it up for grabs for anyone to claim.</p>
        </fieldset>
      {:else}
        <fieldset class="ch-field">
          <legend class="ch-field-label">Assign people (optional)</legend>
          <div class="ch-chip-row" role="group" aria-label="Assign people">
            {#each members as member (member.userId)}
              <button
                type="button"
                class="ch-chip"
                class:active={assignedUserIds.includes(member.userId)}
                aria-pressed={assignedUserIds.includes(member.userId)}
                onclick={() => toggleAssignee(member.userId)}
              >
                {member.displayName}
              </button>
            {/each}
          </div>
          <p class="ch-hint">
            Name who's on this one — assigning is a suggestion: anyone can still join (“I'm in”) or
            step off. Removing someone else needs you to be the chore's minder or creator.
          </p>
        </fieldset>
      {/if}

      {#if chore}
        <fieldset class="ch-field">
          <legend class="ch-field-label">Checklist (optional)</legend>
          {#if liveSubtasks.length > 0}
            <ul class="ch-subtasks">
              {#each liveSubtasks as s (s.id)}
                <li class="ch-subtask-row">
                  <input
                    type="text"
                    class="ch-subtask-input"
                    value={s.title}
                    autocomplete="off"
                    aria-label="Checklist item"
                    onblur={(e) => renameSubtaskItem(s.id, (e.currentTarget as HTMLInputElement).value)}
                    onkeydown={(e) => {
                      if (e.key === 'Enter') {
                        e.preventDefault();
                        (e.currentTarget as HTMLInputElement).blur();
                      }
                    }}
                  />
                  <button
                    type="button"
                    class="ch-subtask-del"
                    onclick={() => boardStore.removeSubtask(chore.id, s.id)}
                    title="Remove this item"
                    aria-label="Remove {s.title}"
                  >
                    ×
                  </button>
                </li>
              {/each}
            </ul>
          {/if}
          <div class="ch-subtask-add">
            <input
              type="text"
              class="ch-subtask-input"
              bind:value={newSubtaskTitle}
              autocomplete="off"
              placeholder="Add an item…"
              aria-label="Add a checklist item"
              onkeydown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  addSubtaskItem();
                }
              }}
            />
            <button
              type="button"
              class="ch-btn-primary ch-subtask-add-btn"
              onclick={addSubtaskItem}
              disabled={!newSubtaskTitle.trim()}
            >
              Add
            </button>
          </div>
          <p class="ch-hint">
            A quick checklist for this chore — anyone can tick items off from the card. It's optional and
            never needed to finish the chore.
          </p>
        </fieldset>
      {/if}

      <label class="ch-field">
        <span class="ch-field-label">Photo (optional)</span>
        {#if existingPhotoPath && !newPhotoFile}
          <p class="ch-hint">Photo attached — pick a new file to replace it.</p>
        {/if}
        <input type="file" accept="image/*" onchange={onPhotoChange} />
        {#if newPhotoFile}
          <span class="ch-hint">{newPhotoFile.name} (will replace existing)</span>
        {/if}
      </label>

      {#if localError}
        <p class="ch-sheet-error" role="alert">{localError}</p>
      {/if}
    </div>

    <footer class="ch-sheet-actions">
      {#if confirmingDelete}
        <span class="ch-delete-confirm-text" role="alert">Delete this chore? This can't be undone.</span>
        <button
          type="button"
          class="ch-btn-ghost"
          onclick={() => (confirmingDelete = false)}
          disabled={deleting}
        >
          Keep it
        </button>
        <button type="button" class="ch-btn-danger" onclick={handleDelete} disabled={deleting}>
          {deleting ? 'Deleting…' : 'Delete'}
        </button>
      {:else}
        <button
          type="button"
          class="ch-btn-danger-ghost ch-delete-trigger"
          onclick={() => (confirmingDelete = true)}
          disabled={submitting}
        >
          Delete
        </button>
        <button type="button" class="ch-btn-ghost" onclick={onClose} disabled={submitting}>
          Cancel
        </button>
        <button type="submit" class="ch-btn-primary" disabled={submitting || !name.trim()}>
          {submitting ? 'Saving…' : 'Save changes'}
        </button>
      {/if}
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

  /* ── Checklist (optional) — manage list within the edit sheet (Phase 14) ── */
  .ch-subtasks {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .ch-subtask-row,
  .ch-subtask-add {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .ch-subtask-input {
    flex: 1;
    min-width: 0;
    font: inherit;
    color: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    min-height: 44px;
  }
  .ch-subtask-input:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
    border-color: var(--color-primary);
  }
  .ch-subtask-del {
    flex-shrink: 0;
    width: 44px;
    height: 44px;
    display: grid;
    place-items: center;
    font-size: 1.25rem;
    line-height: 1;
    color: var(--color-text-muted);
    background: transparent;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    cursor: pointer;
  }
  .ch-subtask-del:hover {
    background: var(--color-action-hover);
    color: var(--color-error);
  }
  .ch-subtask-add-btn {
    flex-shrink: 0;
    min-height: 44px;
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

  /* Destructive delete (irreversible — two-tap confirm). */
  .ch-btn-danger,
  .ch-btn-danger-ghost {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    min-height: 44px;
    font-weight: 500;
    letter-spacing: 0.02em;
  }
  .ch-btn-danger {
    background: var(--color-error);
    color: #fff;
    border: none;
    box-shadow: var(--shadow-1);
  }
  .ch-btn-danger:hover:not(:disabled) {
    filter: brightness(0.92);
  }
  .ch-btn-danger-ghost {
    background: transparent;
    color: var(--color-error);
    border: 1px solid var(--color-error);
  }
  .ch-btn-danger-ghost:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .ch-btn-danger:disabled,
  .ch-btn-danger-ghost:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
  /* Delete sits left; Cancel/Save stay right. */
  .ch-delete-trigger {
    margin-right: auto;
  }
  .ch-delete-confirm-text {
    margin-right: auto;
    align-self: center;
    font-size: 0.875rem;
    color: var(--color-text);
  }
</style>
