<script lang="ts">
  // Settings → Feedback route (/settings/feedback → the admin island's FeedbackApp,
  // dual-mode list/triage). The API enforces site-admin on triage actions — the
  // island renders its own handling. Identity from $lib/session via ctx() (M8).
  import IslandRoute from '$lib/shared/IslandRoute.svelte';
  import FeedbackApp from '$lib/admin/FeedbackApp.svelte';
  import { ctx } from '$lib/session.svelte';
</script>

<IslandRoute>
  <FeedbackApp ctx={ctx({ view: 'feedback' })} />
</IslandRoute>
