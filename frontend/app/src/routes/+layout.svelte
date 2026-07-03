<script lang="ts">
  // The real app shell. Renders the chrome (Header / Nav / MobileBottomNav /
  // Footer) around every route, plus the canonical shared Toasts region. Boots
  // the canonical session store (fetches /api/me once) and the theme store on
  // mount. ssr=false (see +layout.ts) so this all runs client-side.
  import { onMount } from 'svelte';
  import '$lib/styles/app.css';
  import { session } from '$lib/session.svelte';
  import { theme } from '$lib/theme.svelte';
  import Header from '$lib/shell/Header.svelte';
  import Nav from '$lib/shell/Nav.svelte';
  import MobileBottomNav from '$lib/shell/MobileBottomNav.svelte';
  import Footer from '$lib/shell/Footer.svelte';
  import Toasts from '$lib/shared/Toasts.svelte';

  let { children } = $props();

  onMount(() => {
    theme.init();
    session.load();
  });
</script>

<div class="sh-app">
  <Header />

  <div class="sh-body">
    <div class="sh-sidebar">
      <Nav />
    </div>

    <main class="sh-main">
      {#if session.status === 'error'}
        <div class="sh-load-error" role="alert">
          Couldn't load your session{session.error ? `: ${session.error}` : ''}.
          <button type="button" onclick={() => location.reload()}>Retry</button>
        </div>
      {/if}

      <div class="sh-content">
        {@render children()}
      </div>

      <Footer />
    </main>
  </div>

  <div class="sh-mobilenav">
    <MobileBottomNav />
  </div>

  <Toasts />
</div>

<style>
  .sh-app {
    display: flex;
    flex-direction: column;
    min-height: 100vh;
  }
  .sh-body {
    display: flex;
    flex: 1;
    min-height: 0;
  }
  .sh-sidebar {
    display: block;
    flex-shrink: 0;
  }
  .sh-main {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
  }
  .sh-content {
    flex: 1;
    min-width: 0;
  }
  .sh-mobilenav {
    display: none;
  }
  .sh-load-error {
    display: flex;
    align-items: center;
    gap: 12px;
    margin: 12px 16px;
    padding: 10px 14px;
    border-radius: var(--radius-sm);
    background: color-mix(in srgb, var(--color-error) 12%, transparent);
    color: var(--color-error);
    font-size: 0.875rem;
  }
  .sh-load-error button {
    font: inherit;
    font-weight: 600;
    border: none;
    background: transparent;
    color: var(--color-error);
    cursor: pointer;
    text-decoration: underline;
  }

  /* Mobile: drop the sidebar, show the bottom nav (fixed), pad content for it. */
  @media (max-width: 960px) {
    .sh-sidebar {
      display: none;
    }
    .sh-mobilenav {
      display: block;
      position: fixed;
      left: 0;
      right: 0;
      bottom: 0;
      z-index: 90;
    }
    .sh-main {
      padding-bottom: calc(var(--shell-mobile-nav-height, 56px) + env(safe-area-inset-bottom, 0px));
    }
  }
</style>
