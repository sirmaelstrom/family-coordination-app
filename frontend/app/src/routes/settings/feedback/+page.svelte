<script lang="ts">
  // Settings → Feedback route (/settings/feedback → the admin island's FeedbackApp,
  // the old admin-feedback-root, dual-mode list/triage). Identity from $lib/session
  // (M8); the admin ShellContext carries a `view` discriminator. The API enforces
  // site-admin on triage actions — the island renders its own handling.
  import FeedbackApp from '$lib/admin/FeedbackApp.svelte';
  import { session } from '$lib/session.svelte';
</script>

{#if session.ready}
  <FeedbackApp
    ctx={{
      householdId: session.householdId!,
      userId: session.userId!,
      userName: session.userName!,
      view: 'feedback',
    }}
  />
{:else if session.status !== 'error'}
  <p class="route-status">Loading…</p>
{/if}

<style>
  .route-status {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted, #666);
  }
</style>
