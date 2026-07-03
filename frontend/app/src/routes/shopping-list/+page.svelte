<script lang="ts">
  import { onMount } from 'svelte';
  import App from '$lib/shopping-list/App.svelte';
  import type { ShellContext } from '$lib/shopping-list/lib/types';

  let ctx = $state<ShellContext | null>(null);
  let error = $state<string | null>(null);

  onMount(async () => {
    try {
      const res = await fetch('/api/me', {
        credentials: 'include',
        headers: { Accept: 'application/json' },
      });
      if (res.status === 401) {
        // No session on this origin → bounce to the (still server-side) login.
        window.location.href = '/account/login';
        return;
      }
      if (!res.ok) throw new Error(`/api/me returned ${res.status}`);
      const me = await res.json();
      ctx = {
        householdId: me.householdId,
        userId: me.userId,
        userName: me.userName,
        listId: null, // App.svelte auto-selects the first/favorite list when null
      };
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  });
</script>

{#if error}
  <p class="spike-status spike-status--error">Failed to load: {error}</p>
{:else if ctx}
  <App {ctx} />
{:else}
  <p class="spike-status">Loading…</p>
{/if}

<style>
  .spike-status {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted, #666);
  }
  .spike-status--error {
    color: var(--color-error, #e53935);
  }
</style>
