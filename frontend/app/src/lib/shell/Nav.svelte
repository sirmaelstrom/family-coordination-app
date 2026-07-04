<script lang="ts">
  // Desktop side navigation. Order / labels mirror NavMenu.razor. The Settings
  // block is a collapsible section (auto-expands on a /settings route). Every
  // in-app link is built as `${base}${href}`; active state highlights the current
  // path. Hidden on mobile (MobileBottomNav takes over).
  import { base } from '$app/paths';
  import { page } from '$app/state';
  import { session } from '$lib/session.svelte';
  import Icon from './Icon.svelte';
  import {
    primaryNav,
    settingsNav,
    isActive,
    isSettingsActive,
  } from './nav';

  const pathname = $derived(page.url.pathname);
  const settingsActive = $derived(isSettingsActive(pathname, base));

  // Auto-expand the Settings section whenever we're on a settings route;
  // otherwise let the user toggle it.
  let userToggled = $state<boolean | null>(null);
  const settingsOpen = $derived(userToggled ?? settingsActive);

  const visibleSettings = $derived(
    settingsNav.filter((s) => !s.adminOnly || session.isSiteAdmin),
  );
</script>

<nav class="sh-nav" aria-label="Primary">
  <a class="sh-brand" href="{base}/dashboard">
    <Icon name="recipes" size={26} />
    <span class="sh-brand-name">Family Kitchen</span>
  </a>

  <ul class="sh-nav-list">
    {#each primaryNav as item (item.href)}
      <li>
        <a
          class="sh-nav-link"
          class:active={isActive(pathname, base, item.href, item.match)}
          href="{base}{item.href}"
          aria-current={isActive(pathname, base, item.href, item.match) ? 'page' : undefined}
        >
          <Icon name={item.icon} />
          <span>{item.label}</span>
        </a>
      </li>
    {/each}
  </ul>

  <div class="sh-divider"></div>

  <div class="sh-section">
    <button
      type="button"
      class="sh-nav-link sh-section-toggle"
      class:active={settingsActive}
      aria-expanded={settingsOpen}
      onclick={() => (userToggled = !settingsOpen)}
    >
      <Icon name="settings" />
      <span>Settings</span>
      <span class="sh-chevron" class:open={settingsOpen}>
        <Icon name="chevron" size={18} />
      </span>
    </button>

    {#if settingsOpen}
      <ul class="sh-nav-list sh-subnav">
        {#each visibleSettings as item (item.href)}
          <li>
            <a
              class="sh-nav-link sh-sublink"
              class:active={isActive(pathname, base, item.href, 'prefix')}
              href="{base}{item.href}"
              aria-current={isActive(pathname, base, item.href, 'prefix') ? 'page' : undefined}
            >
              <Icon name={item.icon} size={20} />
              <span>{item.label}</span>
            </a>
          </li>
        {/each}
      </ul>
    {/if}
  </div>
</nav>

<style>
  .sh-nav {
    width: var(--shell-nav-width, 240px);
    box-sizing: border-box;
    padding: 8px;
    background: var(--color-drawer);
    border-right: 1px solid var(--color-divider);
    overflow-y: auto;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .sh-brand {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 12px;
    margin-bottom: 8px;
    color: var(--color-primary);
    text-decoration: none;
    border-bottom: 1px solid var(--color-divider);
  }
  .sh-brand-name {
    font-size: 1.1rem;
    font-weight: 600;
    color: var(--color-text);
  }
  .sh-nav-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .sh-nav-link {
    display: flex;
    align-items: center;
    gap: 12px;
    width: 100%;
    padding: 10px 12px;
    border-radius: var(--radius-sm);
    color: var(--color-text);
    text-decoration: none;
    font: inherit;
    font-size: 0.95rem;
    background: transparent;
    border: none;
    cursor: pointer;
    text-align: left;
  }
  .sh-nav-link:hover {
    background: var(--color-action-hover);
  }
  .sh-nav-link.active {
    color: var(--color-primary);
    background: color-mix(in srgb, var(--color-primary) 12%, transparent);
    font-weight: 600;
  }
  .sh-divider {
    height: 1px;
    background: var(--color-divider);
    margin: 8px 4px;
  }
  .sh-section-toggle {
    justify-content: flex-start;
  }
  .sh-chevron {
    margin-left: auto;
    display: inline-flex;
    transition: transform 0.15s ease;
    color: var(--color-text-muted);
  }
  .sh-chevron.open {
    transform: rotate(90deg);
  }
  .sh-subnav {
    margin-top: 2px;
    padding-left: 12px;
  }
  .sh-sublink {
    font-size: 0.9rem;
  }
</style>
