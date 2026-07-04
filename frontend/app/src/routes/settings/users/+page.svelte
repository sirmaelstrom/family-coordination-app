<script lang="ts">
  // Settings → Family Members route (/settings/users → the settings island's
  // UsersApp, the old data-view="users"). Identity from $lib/session (M8);
  // ShellContext carries a `view` discriminator, built from session identity.
  import UsersApp from '$lib/settings/UsersApp.svelte';
  import { session } from '$lib/session.svelte';
</script>

{#if session.ready}
  <UsersApp
    ctx={{
      householdId: session.householdId!,
      userId: session.userId!,
      userName: session.userName!,
      view: 'users',
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
