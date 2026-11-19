// Shared nav model + active-path helper for the shell chrome (Nav + MobileBottomNav).
// Order / labels mirror Components/Layout/NavMenu.razor + MainLayout.razor.
// hrefs are SPA route IDs; callers link with `link(href)`, which adds the base through
// `resolve()` from $app/paths (SvelteKit 3 removed the `base` export).

import { resolve } from '$app/paths';
import type { ResolvedPathname, RouteId, RouteParams } from '$app/types';
import type { IconName } from './Icon.svelte';

/** A route ID with no dynamic segments: `resolve()` needs no params for it. */
export type StaticRouteId = {
  [K in RouteId]: RouteParams<K> extends Record<string, never> ? K : never;
}[RouteId];

/**
 * `resolve()` for a static route held in a union-typed field. resolve's argument type
 * distributes over a union (one tuple per route), so a `StaticRouteId` variable can't be
 * passed to it directly; a literal can. Restricting to static routes keeps the cast honest.
 */
export function link(route: StaticRouteId): ResolvedPathname {
  return (resolve as (route: StaticRouteId) => ResolvedPathname)(route);
}

export interface NavItem {
  href: StaticRouteId;
  label: string;
  icon: IconName;
  match: 'exact' | 'prefix';
}

export interface SettingsItem {
  href: StaticRouteId;
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
  { href: '/settings/deleted-recipes', label: 'Deleted Recipes', icon: 'recipes' },
  { href: '/settings/users', label: 'Family Members', icon: 'people' },
  { href: '/settings/connections', label: 'Connections', icon: 'connections' },
  { href: '/settings/calendar', label: 'Calendar Feed', icon: 'meals' },
  { href: '/settings/agent-access', label: 'Agent Access', icon: 'connections' },
  { href: '/settings/feedback', label: 'Feedback', icon: 'feedback' },
  { href: '/settings/households', label: 'Household Requests', icon: 'admin', adminOnly: true },
];

/** The Settings section landing (mobile bottom-nav Settings target). */
export const SETTINGS_LANDING: StaticRouteId = '/settings/categories';

/** Any /settings/* route (used to highlight/expand the Settings section). Not a route of its
 * own (there is no /settings page), so it is a path segment under the app root. */
export const SETTINGS_PREFIX = 'settings';

/**
 * Whether `href` is the active route. `pathname` is the full location pathname
 * (includes any base); each target is `link(href)`, so it carries the same base.
 * Mirrors NavMenu's NavLinkMatch: 'exact' for Home (also matches the app root),
 * 'prefix' otherwise.
 */
export function isActive(pathname: string, href: StaticRouteId, match: 'exact' | 'prefix'): boolean {
  const target = link(href);
  if (match === 'exact') {
    if (href === '/dashboard') {
      return pathname === target || pathname === resolve('/');
    }
    return pathname === target;
  }
  return pathname === target || pathname.startsWith(`${target}/`);
}

/** Whether the current path is inside the Settings section. */
export function isSettingsActive(pathname: string): boolean {
  const target = resolve('/') + SETTINGS_PREFIX;
  return pathname === target || pathname.startsWith(`${target}/`);
}
