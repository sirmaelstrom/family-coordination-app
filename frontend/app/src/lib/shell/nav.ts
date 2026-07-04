// Shared nav model + active-path helper for the shell chrome (Nav + MobileBottomNav).
// Order / labels mirror Components/Layout/NavMenu.razor + MainLayout.razor.
// hrefs are SPA route paths WITHOUT the base — callers build `${base}${href}`.

import type { IconName } from './Icon.svelte';

export interface NavItem {
  href: string;
  label: string;
  icon: IconName;
  match: 'exact' | 'prefix';
}

export interface SettingsItem {
  href: string;
  label: string;
  icon: IconName;
  adminOnly?: boolean;
}

/** Primary destinations — mirrors NavMenu.razor order (Chores-first). */
export const primaryNav: NavItem[] = [
  { href: '/dashboard', label: 'Home', icon: 'home', match: 'exact' },
  { href: '/chores', label: 'Chores', icon: 'chores', match: 'prefix' },
  { href: '/shopping-list', label: 'Shopping Lists', icon: 'shopping', match: 'prefix' },
  { href: '/meal-plan', label: 'Meal Plan', icon: 'meals', match: 'prefix' },
  { href: '/recipes', label: 'Recipes', icon: 'recipes', match: 'prefix' },
];

/** The Settings section (expands under the side nav). Labels mirror NavMenu.razor. */
export const settingsNav: SettingsItem[] = [
  { href: '/settings/categories', label: 'Categories', icon: 'settings' },
  { href: '/settings/users', label: 'Family Members', icon: 'people' },
  { href: '/settings/connections', label: 'Connections', icon: 'connections' },
  { href: '/settings/feedback', label: 'Feedback', icon: 'feedback' },
  { href: '/settings/households', label: 'Household Requests', icon: 'admin', adminOnly: true },
];

/** The Settings section landing (mobile bottom-nav Settings target). */
export const SETTINGS_LANDING = '/settings/categories';

/** Any /settings/* route (used to highlight/expand the Settings section). */
export const SETTINGS_PREFIX = '/settings';

/**
 * Whether `href` is the active route. `pathname` is the full location pathname
 * (includes base); `base` is $app/paths base ('/app'). Mirrors NavMenu's
 * NavLinkMatch: 'exact' for Home (also matches the base root), 'prefix' otherwise.
 */
export function isActive(
  pathname: string,
  base: string,
  href: string,
  match: 'exact' | 'prefix',
): boolean {
  const target = base + href;
  if (match === 'exact') {
    if (href === '/dashboard') {
      return pathname === target || pathname === base || pathname === `${base}/`;
    }
    return pathname === target;
  }
  return pathname === target || pathname.startsWith(`${target}/`);
}

/** Whether the current path is inside the Settings section. */
export function isSettingsActive(pathname: string, base: string): boolean {
  const target = base + SETTINGS_PREFIX;
  return pathname === target || pathname.startsWith(`${target}/`);
}
