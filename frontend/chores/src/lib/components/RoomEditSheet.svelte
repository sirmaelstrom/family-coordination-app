<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Edit-room sheet (v1.2 — minimal room manager). A bottom sheet to rename a
  // room, change its icon, and set/replace/remove its cover photo. Opened from
  // the Rooms lens drill-in header (real rooms only — never the virtual
  // General group). The full room manager (reorder / delete) is still deferred.
  //
  //  • Photo (council C4 parity with the chore edit-photo flow): if the user
  //    picks a new file we upload it first (POST /api/rooms/{id}/photo →
  //    { photoPath }) then send the returned path in the PUT body. To keep the
  //    existing photo, send the current photoPath unchanged; to remove it, send
  //    null. No file ever goes in the JSON body.
  //
  // A room change is not a chore mutation (no optimistic path), so on success we
  // refetch the ONE board payload via boardStore.reloadBoard() — the dashboard
  // cover / drill hero then render the new photo authoritatively.
  // ───────────────────────────────────────────────────────────────────────
  import type { RoomRollupDto } from '../types';
  import type { RoomUpsertRequest } from '../api';
  import { updateRoom, uploadRoomPhoto } from '../api';
  import { boardStore } from '../state.svelte';
  import { showToast } from '../toasts.svelte';
  import IconPicker from './IconPicker.svelte';

  interface Props {
    open: boolean;
    /** The room being edited (a real room — roomId never null). */
    room: RoomRollupDto | null;
    onClose: () => void;
  }

  let { open, room, onClose }: Props = $props();

  // ── Form state ────────────────────────────────────────────────────────────
  let name = $state('');
  let roomIcon = $state<string>('🧹');
  let existingPhotoPath = $state<string | null>(null);
  let newPhotoFile = $state<File | null>(null);
  /** Set true to drop the existing photo (sends photoPath null). */
  let removePhoto = $state(false);
  let localError = $state<string | null>(null);
  let submitting = $state(false);

  let dialogEl: HTMLDialogElement | null = $state(null);
  let nameInput: HTMLInputElement | null = $state(null);

  // Local object-URL preview of a freshly-picked file (revoked on change/close).
  let previewUrl = $derived(newPhotoFile ? URL.createObjectURL(newPhotoFile) : null);

  function roomToForm(r: RoomRollupDto): void {
    name = r.name;
    roomIcon = r.icon || '🧹';
    existingPhotoPath = r.photoPath;
    newPhotoFile = null;
    removePhoto = false;
    localError = null;
  }

  $effect(() => {
    if (!dialogEl) return;
    if (open && room && !dialogEl.open) {
      roomToForm(room);
      dialogEl.showModal();
      queueMicrotask(() => nameInput?.focus());
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  // Revoke the object URL when the picked file changes or the sheet unmounts.
  $effect(() => {
    const url = previewUrl;
    return () => {
      if (url) URL.revokeObjectURL(url);
    };
  });

  function onPhotoChange(e: Event) {
    const input = e.currentTarget as HTMLInputElement;
    newPhotoFile = input.files && input.files.length > 0 ? input.files[0] : null;
    if (newPhotoFile) removePhoto = false;
  }

  async function handleSubmit(e: SubmitEvent) {
    e.preventDefault();
    if (submitting || !room || room.roomId === null) return;
    const trimmed = name.trim();
    if (!trimmed) {
      localError = 'Give the room a name.';
      return;
    }
    localError = null;
    submitting = true;
    const roomId = room.roomId;

    try {
      // Resolve the photo: remove → null; new file → upload then use the path;
      // otherwise keep what's there. A failed upload keeps the existing photo.
      let resolvedPhotoPath: string | null = existingPhotoPath;
      if (removePhoto) {
        resolvedPhotoPath = null;
      } else if (newPhotoFile) {
        try {
          const uploadResult = await uploadRoomPhoto(roomId, newPhotoFile);
          resolvedPhotoPath = uploadResult.photoPath;
        } catch {
          showToast({ message: 'Photo upload failed — saving other changes.', kind: 'info' });
          resolvedPhotoPath = existingPhotoPath;
        }
      }

      const body: RoomUpsertRequest = {
        name: trimmed,
        icon: roomIcon,
        photoPath: resolvedPhotoPath,
      };
      await updateRoom(roomId, body);
      // Refetch so the dashboard cover / drill hero reflect the change.
      await boardStore.reloadBoard();
      showToast({ message: 'Room updated.', kind: 'success' });
      onClose();
    } catch {
      localError = "Couldn't save the room — try again.";
    } finally {
      submitting = false;
    }
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="ch-sheet">
  <form method="dialog" onsubmit={handleSubmit}>
    <header class="ch-sheet-head">
      <h2>Edit room</h2>
    </header>

    <div class="ch-sheet-body">
      <label class="ch-field">
        <span class="ch-field-label">Room name</span>
        <input
          bind:this={nameInput}
          type="text"
          bind:value={name}
          required
          autocomplete="off"
          placeholder="e.g. Kitchen"
        />
      </label>

      <fieldset class="ch-field">
        <legend class="ch-field-label">Icon</legend>
        <IconPicker value={roomIcon} onSelect={(i) => (roomIcon = i)} label="Room icon" />
      </fieldset>

      <fieldset class="ch-field">
        <legend class="ch-field-label">Cover photo (optional)</legend>
        {#if previewUrl}
          <img class="ch-room-photo-preview" src={previewUrl} alt="New cover preview" />
          <span class="ch-hint">{newPhotoFile?.name} (will replace the current photo)</span>
        {:else if existingPhotoPath && !removePhoto}
          <img class="ch-room-photo-preview" src={existingPhotoPath} alt="Current cover" />
          <span class="ch-hint">Pick a new file to replace it.</span>
        {:else if removePhoto}
          <span class="ch-hint">The cover photo will be removed.</span>
        {/if}
        <input type="file" accept="image/*" onchange={onPhotoChange} />
        {#if existingPhotoPath && !newPhotoFile}
          <button
            type="button"
            class="ch-btn-danger-ghost ch-room-photo-remove"
            onclick={() => (removePhoto = !removePhoto)}
          >
            {removePhoto ? 'Keep current photo' : 'Remove photo'}
          </button>
        {/if}
      </fieldset>

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

  .ch-room-photo-preview {
    width: 100%;
    max-height: 160px;
    object-fit: cover;
    border-radius: var(--radius-sm);
    background: var(--color-action-hover);
    display: block;
  }
  .ch-room-photo-remove {
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
  .ch-btn-danger-ghost {
    font: inherit;
    padding: 8px 16px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    min-height: 40px;
    font-weight: 500;
    letter-spacing: 0.02em;
    background: transparent;
    color: var(--color-error);
    border: 1px solid var(--color-error);
  }
  .ch-btn-danger-ghost:hover {
    background: var(--color-action-hover);
  }
</style>
