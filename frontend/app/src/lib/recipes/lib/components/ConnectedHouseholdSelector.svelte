<script lang="ts">
  // "My Recipes" + a chip per connected household + a "manage connections" link.
  // Mirrors Recipes.razor:55-79. Shown only when connections exist.
  import type { ConnectedHouseholdDto } from '../types';

  interface Props {
    connections: ConnectedHouseholdDto[];
    selectedId: number | null;
    onSelect: (id: number | null) => void;
  }

  let { connections, selectedId, onSelect }: Props = $props();
</script>

<div class="rc-selector">
  <button
    type="button"
    class="rc-chip"
    class:rc-chip-active={selectedId == null}
    onclick={() => onSelect(null)}
  >
    My Recipes
  </button>
  {#each connections as hh (hh.householdId)}
    <button
      type="button"
      class="rc-chip"
      class:rc-chip-active={selectedId === hh.householdId}
      onclick={() => onSelect(hh.householdId)}
    >
      {hh.householdName}
    </button>
  {/each}
  <a
    class="rc-manage"
    href="/settings/connections"
    title="Manage family connections"
    aria-label="Manage family connections">＋</a
  >
</div>

<style>
  .rc-selector {
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
    margin-bottom: 16px;
  }
  .rc-chip {
    font: inherit;
    font-size: 0.8125rem;
    padding: 6px 14px;
    border-radius: 16px;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    color: var(--color-text);
    cursor: pointer;
  }
  .rc-chip:hover {
    background: var(--color-action-hover);
  }
  .rc-chip-active {
    background: var(--color-primary);
    border-color: var(--color-primary);
    color: #fff;
  }
  .rc-chip-active:hover {
    background: var(--color-primary-hover);
  }
  .rc-manage {
    display: grid;
    place-items: center;
    width: 30px;
    height: 30px;
    border-radius: 50%;
    color: var(--color-primary);
    text-decoration: none;
    font-size: 1.1rem;
    line-height: 1;
  }
  .rc-manage:hover {
    background: var(--color-action-hover);
  }
</style>
