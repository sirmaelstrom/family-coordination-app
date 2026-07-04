<script lang="ts">
  // Root of the dashboard island. Reads the ShellContext from #dashboard-root
  // data-attrs (passed by main.ts), loads the read-only aggregate once, and
  // refreshes via liveness (20s visible + refocus). No writes — the only state
  // the island mutates is its own loaded `data`.
  import { untrack } from 'svelte';
  import type { ShellContext } from './lib/types';
  import { dashboardStore as store } from './lib/dashboardStore.svelte';
  import { startLiveness, type LivenessHandle } from './lib/liveness';
  import { showToast } from '$lib/shared/toast-store.svelte';
  import { formatLongDate, formatShortDate } from './lib/dates';
  import ChoreCard from './lib/components/ChoreCard.svelte';
  import ShoppingCard from './lib/components/ShoppingCard.svelte';
  import MealsCard from './lib/components/MealsCard.svelte';
  import QuickActions from './lib/components/QuickActions.svelte';

  let { ctx }: { ctx: ShellContext } = $props();

  let liveness: LivenessHandle | null = null;

  async function load(): Promise<void> {
    await store.load();
  }

  // Background refresh: keep the last good data and surface a calm toast on failure.
  async function reconcile(): Promise<void> {
    const hadData = store.data !== null;
    await store.load();
    if (store.error && hadData) {
      showToast({ message: 'Could not refresh — showing the last update.', kind: 'error' });
    }
  }

  // ⚠ Loop safety (memory svelte5-setup-effect-async-loader-loop): the one-time
  // setup effect calls load(), whose sync prefix reads THEN writes store state.
  // Wrapping the whole body in untrack() gives the effect zero reactive deps so
  // it runs exactly once; liveness + refocus drive every later refresh.
  $effect(() => {
    untrack(() => {
      store.init(ctx);
      void load();
      liveness = startLiveness(() => void reconcile());
    });
    return () => {
      liveness?.stop();
      liveness = null;
    };
  });
</script>

<div class="db-container">
  {#if store.loading && !store.data}
    <div class="db-loading">Loading your dashboard…</div>
  {:else if store.error && !store.data}
    <div class="db-inline-error" role="alert">
      <span>Couldn't load the dashboard.</span>
      <button type="button" class="db-retry" onclick={() => void load()}>Retry</button>
    </div>
  {:else if store.data}
    <div class="db-welcome">
      <h1 class="db-welcome-title">Welcome back, {store.data.greetingName}! 👋</h1>
      <p class="db-welcome-sub">{store.data.householdName} • {formatLongDate(store.data.today)}</p>
    </div>

    <div class="db-grid">
      <ChoreCard chores={store.data.chores} attention={store.choreAttention} />
      <ShoppingCard shopping={store.data.shopping} progress={store.shoppingProgress} />
      <MealsCard groups={store.mealsByType} dateLabel={formatShortDate(store.data.today)} />
    </div>

    <QuickActions />
  {/if}
</div>
