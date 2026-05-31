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
  //   positive integer; daysOfWeek is a weekday CSV. anchorDate is sent
  //   through unchanged from the DTO (we don't recompute it).
  // ───────────────────────────────────────────────────────────────────────
  import type { EffortTier, RecurrenceMode, MemberDto, RoomRollupDto, ChoreDto } from '../types';
  import type { UpdateChoreRequest } from '../api';
  import { boardStore } from '../state.svelte';
  import { uploadChorePhoto } from '../api';
  import { showToast } from '../toasts.svelte';

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
  let cadence = $state<Cadence>('once');
  let intervalDays = $state('3');
  let selectedDays = $state(new Set<string>());
  let effort = $state<EffortTier>('Standard');
  let roomId = $state<number | null>(null);
  let ownerUserId = $state<number | null>(null);
  let existingPhotoPath = $state<string | null>(null);
  let newPhotoFile = $state<File | null>(null);
  let localError = $state<string | null>(null);
  let submitting = $state(false);

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

  let roomChips = $derived([...rooms].sort((a, b) => a.sortOrder - b.sortOrder));

  /** Derive the cadence + anchorDate from the ChoreDto when pre-filling. */
  function choreToFormState(c: ChoreDto): void {
    name = c.name;
    description = c.description ?? '';
    roomId = c.roomId ?? null;
    effort = c.effortTier;
    ownerUserId = c.ownerUserId ?? null;
    existingPhotoPath = c.photoPath;
    newPhotoFile = null;
    localError = null;

    // Map recurrenceMode → cadence (D4-B; no monthly-on-day).
    // Note: ChoreDto does not carry intervalDays or daysOfWeek — those are
    // write-only request fields. We can pre-fill the cadence type from
    // recurrenceMode, but the sub-values (N days / weekday selection) must be
    // entered fresh by the user each edit.
    switch (c.recurrenceMode) {
      case 'OneOff':
        cadence = 'once';
        intervalDays = '3';
        selectedDays = new Set<string>();
        break;
      case 'Flexible':
        cadence = 'everyN';
        intervalDays = '3';
        selectedDays = new Set<string>();
        break;
      case 'Fixed':
        cadence = 'days';
        intervalDays = '3';
        selectedDays = new Set<string>();
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

  function onPhotoChange(e: Event) {
    const input = e.currentTarget as HTMLInputElement;
    newPhotoFile = input.files && input.files.length > 0 ? input.files[0] : null;
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
        roomId,
        recurrenceMode: recurrence.mode,
        intervalDays: recurrence.intervalDays,
        // anchorDate: pass null — no client date math (MN9). The server manages
        // anchor dates for recurrence; we don't re-derive them here.
        anchorDate: null,
        daysOfWeek: recurrence.daysOfWeek,
        dayOfMonth: null,
        effortTier: effort,
        ownerUserId,
        version: chore.version,
        photoPath: resolvedPhotoPath,
      };

      await boardStore.edit(chore.id, body);
      onClose();
    } finally {
      submitting = false;
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

        {#if cadence === 'everyN'}
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
        </div>
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

      <!-- Assignment is NOT editable (v1.0 D6 — use Claim / Hand off on the card). -->

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
      <button type="button" class="ch-btn-ghost" onclick={onClose} disabled={submitting}>
        Cancel
      </button>
      <button type="submit" class="ch-btn-primary" disabled={submitting || !name.trim()}>
        {submitting ? 'Saving…' : 'Save changes'}
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
