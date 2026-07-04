<script lang="ts">
  // Quick-action buttons — parity with Home.razor lines 209-263. Each href is a
  // base-relative SPA route ($app/paths base); SvelteKit intercepts the anchor
  // for client-side nav. The /recipes/new shape matches the recipes routes (WP-07).
  import { base } from '$app/paths';

  const ACTIONS = [
    { href: '/recipes/new', icon: '➕', label: 'New Recipe' },
    { href: '/recipes', icon: '📖', label: 'Browse Recipes' },
    { href: '/meal-plan', icon: '🗓️', label: 'This Week' },
    { href: '/chores', icon: '🧹', label: 'Chores' },
    { href: '/settings/users', icon: '👤', label: 'Invite Family' },
  ];
</script>

<section class="db-quick">
  <h2 class="db-quick-title">Quick Actions</h2>
  <div class="db-quick-grid">
    {#each ACTIONS as action (action.href)}
      <a class="db-quick-btn" href={`${base}${action.href}`}>
        <span aria-hidden="true">{action.icon}</span>
        <span>{action.label}</span>
      </a>
    {/each}
  </div>
</section>
