<script lang="ts">
  // Top app bar. Mirrors MainLayout.razor's MudAppBar: brand title, right-aligned
  // presence (placeholder — wired in WP-02), dark-mode toggle, site-admin chip,
  // user name + avatar, logout. Identity comes from the canonical session store;
  // dark mode from the theme store.
  import { onMount } from 'svelte';
  import { session } from '$lib/session.svelte';
  import { theme } from '$lib/theme.svelte';
  import { presence } from '$lib/presence.svelte';
  import { sidebar } from './sidebar.svelte';
  import Icon from './Icon.svelte';
  import UserAvatar from '$lib/shared/UserAvatar.svelte';

  // The header is the single always-present consumer of presence, so it owns the
  // poller lifecycle: start the 30s heartbeat + roster poll on mount, stop on teardown.
  onMount(() => {
    presence.start();
    return () => presence.stop();
  });
</script>

<header class="sh-header">
  <!-- Toggles the desktop side-nav mini rail (parity with the old MudDrawer
       hamburger). Desktop-only — mobile uses the bottom nav, no side rail. -->
  <button
    type="button"
    class="sh-icon-btn sh-nav-toggle"
    onclick={() => sidebar.toggle()}
    aria-expanded={!sidebar.collapsed}
    aria-label={sidebar.collapsed ? 'Expand navigation' : 'Collapse navigation'}
    title="Toggle menu"
  >
    <Icon name="menu" />
  </button>

  <div class="sh-header-brand">Family Kitchen</div>

  <div class="sh-header-spacer"></div>

  <div class="sh-header-actions">
    <!-- Online-users roster + client-derived sync indicator (WP-02). -->
    <div class="sh-presence">
      {#each presence.users.slice(0, 5) as u (u.userId)}
        <span
          class="sh-presence-avatar"
          class:sh-presence-away={u.status === 'away'}
          title={u.status === 'away' ? `${u.displayName} (away)` : u.displayName}
        >
          <UserAvatar name={u.displayName} initials={u.initials} pictureUrl={u.pictureUrl} size={24} />
        </span>
      {/each}
      <span
        class="sh-sync"
        class:sh-sync-offline={!presence.online}
        title={presence.online ? 'Synced' : 'Offline — reconnecting…'}
        aria-label={presence.online ? 'Synced' : 'Offline'}
      ></span>
    </div>

    <button
      type="button"
      class="sh-icon-btn"
      onclick={() => theme.toggle()}
      title={theme.dark ? 'Switch to light mode' : 'Switch to dark mode'}
      aria-label={theme.dark ? 'Switch to light mode' : 'Switch to dark mode'}
    >
      <Icon name={theme.dark ? 'light' : 'dark'} />
    </button>

    {#if session.isSiteAdmin}
      <span class="sh-admin-chip">
        <Icon name="admin" size={16} />
        Admin
      </span>
    {/if}

    {#if session.user}
      <span class="sh-user">
        <UserAvatar
          name={session.user.userName}
          initials={session.user.initials}
          pictureUrl={session.user.pictureUrl}
          size={28}
        />
        <span class="sh-user-name">{session.user.userName}</span>
      </span>
    {/if}

    <a class="sh-icon-btn" href="/account/logout" title="Sign out" aria-label="Sign out">
      <Icon name="logout" />
    </a>
  </div>
</header>

<style>
  .sh-header {
    height: var(--shell-appbar-height, 56px);
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 0 12px;
    background: var(--color-appbar);
    color: #fff;
    box-shadow: var(--shadow-1);
  }
  .sh-header-brand {
    font-size: 1.15rem;
    font-weight: 600;
    white-space: nowrap;
  }
  .sh-header-spacer {
    flex: 1;
  }
  .sh-header-actions {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .sh-presence {
    display: flex;
    align-items: center;
    gap: 4px;
  }
  .sh-presence-avatar {
    display: inline-flex;
    border-radius: 50%;
    margin-left: -6px;
    box-shadow: 0 0 0 2px var(--color-appbar);
  }
  .sh-presence-avatar:first-child {
    margin-left: 0;
  }
  .sh-presence-away {
    opacity: 0.5;
  }
  .sh-sync {
    width: 8px;
    height: 8px;
    margin-left: 6px;
    border-radius: 50%;
    background: var(--color-success, #43a047);
    box-shadow: 0 0 0 2px rgba(255, 255, 255, 0.25);
  }
  .sh-sync-offline {
    background: var(--color-text-disabled, #9e9e9e);
  }
  .sh-icon-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 40px;
    height: 40px;
    border-radius: 50%;
    border: none;
    background: transparent;
    color: inherit;
    cursor: pointer;
    text-decoration: none;
  }
  .sh-icon-btn:hover {
    background: rgba(255, 255, 255, 0.15);
  }
  .sh-admin-chip {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    padding: 3px 10px;
    border: 1px solid var(--color-warning);
    color: var(--color-warning);
    border-radius: 999px;
    font-size: 0.75rem;
    font-weight: 600;
    background: rgba(255, 255, 255, 0.08);
  }
  .sh-user {
    display: inline-flex;
    align-items: center;
    gap: 8px;
  }
  .sh-user-name {
    font-size: 0.875rem;
    white-space: nowrap;
  }
  /* Mobile: collapse the presence + admin chip + name to keep the bar tidy,
     and drop the nav toggle (no side rail on mobile — the bottom nav owns it). */
  @media (max-width: 960px) {
    .sh-presence,
    .sh-admin-chip,
    .sh-user-name,
    .sh-nav-toggle {
      display: none;
    }
  }
</style>
