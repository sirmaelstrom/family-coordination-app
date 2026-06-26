<script lang="ts">
  // Global island CSS variables (--color-primary, --radius-*, --shadow-*, etc.) that the
  // shopping-list components consume. In the real shell this becomes the consolidated design
  // system; for the spike we just pull the island's own stylesheet so it renders at parity.
  import '$lib/shopping-list/styles/app.css';

  let { children } = $props();
</script>

<div class="spike-shell">
  <header class="spike-header">
    <strong>Family Kitchen</strong>
    <span class="spike-badge">SvelteKit SPA · de-Blazor spike</span>
  </header>
  <main class="spike-main">
    {@render children()}
  </main>
</div>

<style>
  .spike-shell {
    min-height: 100vh;
  }
  .spike-header {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 12px 16px;
    background: var(--color-primary, #2e7d32);
    color: #fff;
  }
  .spike-badge {
    font-size: 0.75rem;
    opacity: 0.85;
    border: 1px solid rgba(255, 255, 255, 0.5);
    border-radius: 999px;
    padding: 2px 10px;
  }
  .spike-main {
    min-height: calc(100vh - 49px);
  }
</style>
