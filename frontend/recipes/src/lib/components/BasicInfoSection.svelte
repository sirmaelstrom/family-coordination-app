<script lang="ts">
  // Basic-info form section (mirrors RecipeEdit.razor:73-131): name (required),
  // description, prep/cook/servings, source URL, and the recipe-type select.
  // Also shows the "Imported from {domain}" notice for an edited imported recipe.
  import { recipeEditStore } from '../recipeEditStore.svelte';
  import { RECIPE_TYPES, recipeTypeLabel } from '../recipeType';

  const store = recipeEditStore;

  function domainOf(url: string): string {
    try {
      return new URL(url).host.replace(/^www\./, '');
    } catch {
      return url;
    }
  }

  function isSafeUrl(url: string): boolean {
    try {
      const u = new URL(url);
      return u.protocol === 'http:' || u.protocol === 'https:';
    } catch {
      return false;
    }
  }
</script>

<section class="rc-paper">
  {#if store.isEdit && store.form.sourceUrl.trim()}
    <div class="rc-import-note">
      <span aria-hidden="true">☁</span>
      <span>
        Imported from
        {#if isSafeUrl(store.form.sourceUrl)}
          <a href={store.form.sourceUrl} target="_blank" rel="noopener noreferrer"
            >{domainOf(store.form.sourceUrl)}</a
          >
        {:else}
          {domainOf(store.form.sourceUrl)}
        {/if}
      </span>
    </div>
  {/if}

  <h2 class="rc-section-title">Basic Info</h2>

  <label class="rc-field">
    <span class="rc-field-label">Recipe Name <span class="rc-req">*</span></span>
    <input
      type="text"
      class="rc-input"
      bind:value={store.form.name}
      oninput={() => store.onEdit()}
      required
    />
  </label>

  <label class="rc-field">
    <span class="rc-field-label">Description</span>
    <textarea
      class="rc-input rc-textarea"
      rows="2"
      bind:value={store.form.description}
      oninput={() => store.onEdit()}
      placeholder="Brief introduction to the recipe"
    ></textarea>
  </label>

  <div class="rc-row3">
    <label class="rc-field">
      <span class="rc-field-label">Prep Time (min)</span>
      <input
        type="number"
        class="rc-input"
        min="0"
        bind:value={store.form.prepTimeMinutes}
        oninput={() => store.onEdit()}
      />
    </label>
    <label class="rc-field">
      <span class="rc-field-label">Cook Time (min)</span>
      <input
        type="number"
        class="rc-input"
        min="0"
        bind:value={store.form.cookTimeMinutes}
        oninput={() => store.onEdit()}
      />
    </label>
    <label class="rc-field">
      <span class="rc-field-label">Servings</span>
      <input
        type="number"
        class="rc-input"
        min="1"
        bind:value={store.form.servings}
        oninput={() => store.onEdit()}
      />
    </label>
  </div>

  <label class="rc-field">
    <span class="rc-field-label">Source URL (optional)</span>
    <input
      type="url"
      class="rc-input"
      bind:value={store.form.sourceUrl}
      oninput={() => store.onEdit()}
      placeholder="Link to original recipe"
    />
  </label>

  <label class="rc-field">
    <span class="rc-field-label">Recipe Type</span>
    <select class="rc-input" bind:value={store.form.recipeType} onchange={() => store.onEdit()}>
      {#each RECIPE_TYPES as type (type)}
        <option value={type}>{recipeTypeLabel(type)}</option>
      {/each}
    </select>
  </label>
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
  .rc-import-note {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 10px 14px;
    margin-bottom: 16px;
    border-radius: var(--radius-sm);
    background: rgba(30, 136, 229, 0.08);
    border-left: 4px solid var(--color-info);
    font-size: 0.875rem;
  }
  .rc-import-note a {
    color: var(--color-info);
  }
  .rc-field {
    display: flex;
    flex-direction: column;
    gap: 4px;
    margin-bottom: 12px;
  }
  .rc-field-label {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .rc-req {
    color: var(--color-error);
  }
  .rc-input {
    font: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    color: var(--color-text);
    width: 100%;
  }
  .rc-input:focus {
    outline: none;
    border-color: var(--color-primary);
  }
  .rc-textarea {
    resize: vertical;
    min-height: 60px;
  }
  .rc-row3 {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 12px;
  }
  @media (max-width: 600px) {
    .rc-row3 {
      grid-template-columns: 1fr;
    }
  }
</style>
