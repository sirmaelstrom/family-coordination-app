<script lang="ts">
  import { base } from '$app/paths';
</script>

<section class="spike-index">
  <h1>De-Blazor spike</h1>
  <p>The shopping-list island, rendered as a SvelteKit route over <code>/api</code>.</p>
  <a class="spike-link" href="{base}/shopping-list">Open shopping list →</a>
</section>

<style>
  .spike-index {
    max-width: 640px;
    margin: 0 auto;
    padding: 48px 16px;
  }
  .spike-link {
    display: inline-block;
    margin-top: 16px;
    font-weight: 600;
    color: var(--color-primary, #2e7d32);
  }
</style>
