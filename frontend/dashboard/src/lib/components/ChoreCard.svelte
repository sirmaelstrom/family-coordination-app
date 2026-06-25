<script lang="ts">
  // Chores summary card — parity with Home.razor lines 27-103. Read-only; the
  // arrow + empty CTA both navigate to /chores (full-document anchor nav).
  import type { DashboardChoreSummaryDto } from '../types';

  let { chores, attention }: { chores: DashboardChoreSummaryDto; attention: number } = $props();
</script>

<div class="db-card">
  <div class="db-card-header">
    <div class="db-card-avatar" aria-hidden="true">🧹</div>
    <div class="db-card-headings">
      <h2 class="db-card-title">Chores</h2>
      <p class="db-card-status">
        {#if chores.activeTotal === 0}
          No chores yet
        {:else if attention > 0}
          {attention} need attention
        {:else}
          All caught up
        {/if}
      </p>
    </div>
    <a class="db-card-arrow" href="/chores" aria-label="Go to chores">→</a>
  </div>

  <div class="db-card-body">
    {#if chores.activeTotal === 0}
      <p class="db-empty">No chores set up yet</p>
      <div class="db-empty-action">
        <a class="db-link" href="/chores">Set up the chore board</a>
      </div>
    {:else if attention === 0 && chores.upForGrabs === 0}
      <p class="db-empty">All caught up — nothing needs attention 🎉</p>
    {:else}
      <div>
        {#if chores.overdue > 0}
          <div class="db-stat-row">
            <span class="db-stat-icon db-stat-overdue" aria-hidden="true">⚠️</span>
            <span><b>{chores.overdue}</b> overdue</span>
          </div>
        {/if}
        {#if chores.dueToday > 0}
          <div class="db-stat-row">
            <span class="db-stat-icon db-stat-due" aria-hidden="true">📅</span>
            <span><b>{chores.dueToday}</b> due today</span>
          </div>
        {/if}
        {#if chores.upForGrabs > 0}
          <div class="db-stat-row">
            <span class="db-stat-icon" aria-hidden="true">✋</span>
            <span><b>{chores.upForGrabs}</b> up for grabs</span>
          </div>
        {/if}
      </div>
    {/if}
  </div>
</div>
