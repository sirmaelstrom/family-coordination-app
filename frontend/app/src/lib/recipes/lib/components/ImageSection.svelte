<script lang="ts">
  // Image form section (mirrors RecipeEdit.razor:133-177): preview + remove,
  // file upload (multipart via the store), and "Choose Existing" (opens the
  // household image picker, hosted by EditApp).
  import { recipeEditStore } from '../recipeEditStore.svelte';

  interface Props {
    onChooseExisting: () => void;
  }

  let { onChooseExisting }: Props = $props();

  const store = recipeEditStore;

  let fileInput: HTMLInputElement | null = $state(null);

  async function onFileChange(e: Event): Promise<void> {
    const input = e.currentTarget as HTMLInputElement;
    const file = input.files?.[0];
    if (file) await store.uploadImage(file);
    input.value = ''; // allow re-selecting the same file
  }
</script>

<section class="rc-paper">
  <h2 class="rc-section-title">Image</h2>

  {#if store.form.imagePath}
    <div class="rc-image-preview">
      <img src={store.form.imagePath} alt="Recipe" />
      <button type="button" class="rc-image-remove" aria-label="Remove image" onclick={() => store.removeImage()}
        >×</button
      >
    </div>
  {/if}

  <div class="rc-image-actions">
    <button type="button" class="rc-btn-outline" onclick={() => fileInput?.click()}>
      {store.form.imagePath ? 'Change Image' : 'Upload Image'}
    </button>
    <button type="button" class="rc-btn-outline rc-secondary" onclick={onChooseExisting}>
      Choose Existing
    </button>
    <input
      bind:this={fileInput}
      type="file"
      accept="image/*"
      class="rc-file-hidden"
      onchange={onFileChange}
    />
  </div>
  <p class="rc-image-hint">Max 10 MB. Supported: JPG, PNG, GIF, WebP</p>
</section>

<style>
  .rc-paper {
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-1);
    padding: 16px;
    margin-bottom: 16px;
  }
  .rc-section-title {
    margin: 0 0 12px;
    font-size: 1.125rem;
    font-weight: 500;
  }
  .rc-image-preview {
    position: relative;
    display: inline-block;
    margin-bottom: 12px;
  }
  .rc-image-preview img {
    width: 300px;
    max-width: 100%;
    height: 200px;
    object-fit: cover;
    border-radius: var(--radius-sm);
    box-shadow: var(--shadow-2);
    display: block;
  }
  .rc-image-remove {
    position: absolute;
    top: 4px;
    right: 4px;
    width: 28px;
    height: 28px;
    display: grid;
    place-items: center;
    border: none;
    border-radius: 50%;
    background: rgba(0, 0, 0, 0.55);
    color: #fff;
    font-size: 1.2rem;
    line-height: 1;
    cursor: pointer;
  }
  .rc-image-remove:hover {
    background: rgba(0, 0, 0, 0.75);
  }
  .rc-image-actions {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
  }
  .rc-file-hidden {
    display: none;
  }
  .rc-image-hint {
    margin: 6px 0 0;
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .rc-btn-outline {
    font: inherit;
    padding: 10px 18px;
    border-radius: var(--radius-sm);
    border: 1px solid var(--color-primary);
    background: transparent;
    color: var(--color-primary);
    cursor: pointer;
    min-height: 40px;
    font-weight: 500;
  }
  .rc-btn-outline:hover {
    background: var(--color-action-hover);
  }
  .rc-secondary {
    border-color: var(--color-secondary);
    color: var(--color-secondary);
  }
</style>
