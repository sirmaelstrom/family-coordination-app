<script lang="ts">
  // Household image picker (mirrors ImagePickerDialog.razor): a grid of the
  // household's uploaded images (GET /images), click to select, confirm to apply.
  import { listImages, ApiError } from '../api';

  interface Props {
    open: boolean;
    currentImage: string | null;
    onClose: () => void;
    onSelect: (path: string) => void;
  }

  let { open, currentImage, onClose, onSelect }: Props = $props();

  let dialogEl: HTMLDialogElement | null = $state(null);
  let images = $state<string[]>([]);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let selected = $state<string | null>(null);
  // Re-fetch once per open (the grid can change between opens).
  let loadedWhileOpen = false;

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      dialogEl.showModal();
      if (!loadedWhileOpen) {
        loadedWhileOpen = true;
        selected = currentImage;
        void load();
      }
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
    if (!open) loadedWhileOpen = false;
  });

  async function load(): Promise<void> {
    loading = true;
    error = null;
    try {
      images = await listImages();
    } catch (e) {
      error = e instanceof ApiError ? `Couldn't load images (HTTP ${e.status}).` : "Couldn't load images.";
    } finally {
      loading = false;
    }
  }

  function toggle(image: string): void {
    selected = selected === image ? null : image;
  }

  function confirm(): void {
    if (selected) {
      onSelect(selected);
      onClose();
    }
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="rc-dialog">
  {#if open}
    <header class="rc-dialog-head">
      <h2>Select Image</h2>
      <button type="button" class="rc-dialog-x" aria-label="Close" onclick={onClose}>×</button>
    </header>

    <div class="rc-dialog-body">
      {#if loading}
        <div class="rc-picker-status">Loading images…</div>
      {:else if error}
        <div class="rc-picker-error" role="alert">{error}</div>
      {:else if images.length === 0}
        <div class="rc-picker-status">No images found. Upload an image first using the file picker.</div>
      {:else}
        <p class="rc-picker-intro">Click an image to select it. These are all images uploaded for your household.</p>
        <div class="rc-picker-grid">
          {#each images as image (image)}
            <button
              type="button"
              class="rc-picker-item"
              class:rc-picker-selected={selected === image}
              onclick={() => toggle(image)}
              aria-pressed={selected === image}
            >
              <img src={image} alt="Recipe option" />
              {#if selected === image}
                <span class="rc-picker-check" aria-hidden="true">✓</span>
              {/if}
            </button>
          {/each}
        </div>
      {/if}
    </div>

    <footer class="rc-dialog-actions">
      <button type="button" class="rc-btn-ghost" onclick={onClose}>Cancel</button>
      <button type="button" class="rc-btn-solid" onclick={confirm} disabled={!selected}>Select Image</button>
    </footer>
  {/if}
</dialog>

<style>
  .rc-dialog[open] {
    position: fixed;
    inset: 0;
    margin: auto;
    border: none;
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    padding: 0;
    width: min(640px, calc(100vw - 32px));
    max-height: calc(100vh - 32px);
    box-shadow: var(--shadow-4);
    display: flex;
    flex-direction: column;
  }
  .rc-dialog::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .rc-dialog-head {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 16px 16px 12px 24px;
    border-bottom: 1px solid var(--color-line);
  }
  .rc-dialog-head h2 {
    margin: 0;
    flex: 1;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .rc-dialog-x {
    flex-shrink: 0;
    width: 36px;
    height: 36px;
    display: grid;
    place-items: center;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    border-radius: var(--radius-sm);
    font-size: 1.4rem;
    line-height: 1;
    cursor: pointer;
  }
  .rc-dialog-x:hover {
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .rc-dialog-body {
    overflow-y: auto;
    padding: 16px 24px;
    flex: 1;
  }
  .rc-picker-intro {
    margin: 0 0 12px;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .rc-picker-status {
    padding: 24px 0;
    text-align: center;
    color: var(--color-text-muted);
  }
  .rc-picker-error {
    padding: 12px 16px;
    border-left: 4px solid var(--color-error);
    background: rgba(229, 57, 53, 0.08);
    color: var(--color-error);
    border-radius: var(--radius-sm);
    font-size: 0.875rem;
  }
  .rc-picker-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(120px, 1fr));
    gap: 12px;
  }
  .rc-picker-item {
    position: relative;
    padding: 0;
    aspect-ratio: 1;
    border-radius: 8px;
    overflow: hidden;
    cursor: pointer;
    border: 3px solid transparent;
    background: var(--color-action-hover);
  }
  .rc-picker-item:hover {
    border-color: var(--color-primary);
  }
  .rc-picker-selected {
    border-color: var(--color-success);
  }
  .rc-picker-item img {
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
  }
  .rc-picker-check {
    position: absolute;
    inset: 0;
    display: grid;
    place-items: center;
    background: rgba(0, 0, 0, 0.3);
    color: var(--color-success);
    font-size: 2rem;
  }
  .rc-dialog-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 12px 24px calc(16px + env(safe-area-inset-bottom, 0px));
    border-top: 1px solid var(--color-line);
  }
  .rc-btn-ghost,
  .rc-btn-solid {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 44px;
    font-weight: 500;
  }
  .rc-btn-ghost {
    background: transparent;
    color: var(--color-text-muted);
  }
  .rc-btn-ghost:hover {
    background: var(--color-action-hover);
  }
  .rc-btn-solid {
    background: var(--color-primary);
    color: #fff;
  }
  .rc-btn-solid:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .rc-btn-solid:disabled {
    opacity: 0.6;
    cursor: default;
  }
</style>
