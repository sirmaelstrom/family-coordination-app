<script lang="ts">
  // Settings → Household Requests route (/settings/households → the admin island's
  // HouseholdsApp, the old admin-households-root). SITE-ADMIN ONLY: the households
  // nav entry is already gated on session.isSiteAdmin (shell Nav), and a non-admin
  // who reaches the route gets the API's 403 surfaced as the island's built-in
  // "Access denied" panel (R-C4) inside the shell — never a blank page. Identity
  // from $lib/session (M8); the admin ShellContext carries a `view` discriminator.
  import HouseholdsApp from '$lib/admin/HouseholdsApp.svelte';
  import { session } from '$lib/session.svelte';
</script>

{#if session.ready}
  <HouseholdsApp
    ctx={{
      householdId: session.householdId!,
      userId: session.userId!,
      userName: session.userName!,
      view: 'households',
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
