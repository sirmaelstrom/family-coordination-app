<script lang="ts">
  // Shopping list summary card — parity with Home.razor lines 106-155.
  import type { DashboardShoppingSummaryDto } from '../types';

  let { shopping, progress }: { shopping: DashboardShoppingSummaryDto; progress: number } = $props();
</script>

<div class="db-card">
  <div class="db-card-header">
    <div class="db-card-avatar" aria-hidden="true">🛒</div>
    <div class="db-card-headings">
      <h2 class="db-card-title">Shopping List</h2>
      <p class="db-card-status">
        {#if shopping.remaining > 0}
          {shopping.remaining} items remaining
        {:else}
          All done!
        {/if}
      </p>
    </div>
    <a class="db-card-arrow" href="/shopping-list" aria-label="Go to shopping list">→</a>
  </div>

  <div class="db-card-body">
    {#if shopping.total === 0}
      <p class="db-empty">Shopping list is empty</p>
      <div class="db-empty-action">
        <a class="db-link" href="/shopping-list">Generate from meal plan</a>
      </div>
    {:else}
      <div class="db-progress" role="progressbar" aria-valuenow={progress} aria-valuemin="0" aria-valuemax="100">
        <div class="db-progress-fill" style="width: {progress}%"></div>
      </div>
      <p class="db-progress-caption">
        {shopping.checked} of {shopping.total} items checked
      </p>
    {/if}
  </div>
</div>
