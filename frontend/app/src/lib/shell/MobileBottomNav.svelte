<script lang="ts">
  // Mobile bottom navigation. Mirrors MainLayout.razor:132-170 — Home · Chores ·
  // Shopping · Meals · Recipes · Settings, where Settings links to the section
  // landing (/settings/categories). Shown on mobile only.
  import { base } from '$app/paths';
  import { page } from '$app/state';
  import Icon from './Icon.svelte';
  import { primaryNav, isActive, isSettingsActive, SETTINGS_LANDING } from './nav';

  const pathname = $derived(page.url.pathname);
  const settingsActive = $derived(isSettingsActive(pathname, base));
</script>

<nav class="sh-bottom-nav" aria-label="Primary (mobile)">
  {#each primaryNav as item (item.href)}
    <a
      class="sh-bottom-link"
      class:active={isActive(pathname, base, item.href, item.match)}
      href="{base}{item.href}"
      aria-label={item.label}
      aria-current={isActive(pathname, base, item.href, item.match) ? 'page' : undefined}
    >
      <Icon name={item.icon} size={24} />
    </a>
  {/each}
  <a
    class="sh-bottom-link"
    class:active={settingsActive}
    href="{base}{SETTINGS_LANDING}"
    aria-label="Settings"
    aria-current={settingsActive ? 'page' : undefined}
  >
    <Icon name="settings" size={24} />
  </a>
</nav>

<style>
  .sh-bottom-nav {
    display: flex;
    justify-content: space-around;
    align-items: center;
    height: calc(var(--shell-mobile-nav-height, 56px) + env(safe-area-inset-bottom, 0px));
    padding-bottom: env(safe-area-inset-bottom, 0px);
    background: var(--color-surface);
    border-top: 1px solid var(--color-divider);
    box-shadow: var(--shadow-4);
  }
  .sh-bottom-link {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    flex: 1;
    height: 100%;
    color: var(--color-text-muted);
    text-decoration: none;
  }
  .sh-bottom-link.active {
    color: var(--color-primary);
  }
  .sh-bottom-link:hover {
    color: var(--color-text);
  }
  .sh-bottom-link.active:hover {
    color: var(--color-primary);
  }
</style>
