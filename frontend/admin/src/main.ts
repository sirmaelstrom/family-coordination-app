import { mount as svelteMount, unmount } from 'svelte';
import type { Component } from 'svelte';
import HouseholdsApp from './HouseholdsApp.svelte';
import FeedbackApp from './FeedbackApp.svelte';
import './styles/app.css';
import type { ShellContext } from './lib/types';

type MountedApp = ReturnType<typeof svelteMount>;

interface Instance {
  app: MountedApp;
  /** The exact node we mounted into — lets us detect a Blazor resume that
   *  replaced or emptied the root out from under us. */
  el: HTMLElement;
}

// Blazor Server inserts component output via DOM diff, and inline <script>
// tags in that output are NOT executed by the browser. The Razor shells
// (HouseholdAdmin.razor / FeedbackAdmin.razor) use IJSRuntime.InvokeAsync("import", …)
// to load this module, then call the global mounter below. Exposing a named
// entry on window is the simplest way to survive enhanced-nav round-trips.
declare global {
  interface Window {
    AdminIsland?: {
      mount(rootId: string): void;
      destroy(rootId: string): void;
    };
  }
}

// One bundle serves BOTH admin routes (spec D1): /settings/households mounts the
// households root, /settings/feedback mounts the feedback root. The root's
// `data-view` attr picks the component; only one root is present per page.
const HOUSEHOLDS_ROOT_ID = 'admin-households-root';
const FEEDBACK_ROOT_ID = 'admin-feedback-root';
const ALL_ROOT_IDS = [HOUSEHOLDS_ROOT_ID, FEEDBACK_ROOT_ID];

const instances = new Map<string, Instance>();

// Roots the Razor shell has asked us to mount. We re-assert these when the page
// is restored so a Blazor circuit resume / enhanced-nav merge that wiped our
// content can't leave the island permanently blank (see runHeal below).
const desiredRoots = new Set<string>();

function readContext(el: HTMLElement): ShellContext {
  const householdId = Number(el.dataset.householdId ?? '0');
  const userId = Number(el.dataset.userId ?? '0');
  const userName = el.dataset.userName ?? '';
  const view = el.dataset.view === 'feedback' ? 'feedback' : 'households';
  if (!householdId || !userId) {
    throw new Error('admin: missing data-household-id / data-user-id');
  }
  return { householdId, userId, userName, view };
}

function componentForView(view: ShellContext['view']): Component<{ ctx: ShellContext }> {
  return (view === 'feedback' ? FeedbackApp : HouseholdsApp) as Component<{ ctx: ShellContext }>;
}

// Track dark-mode state and mirror it as a class on the island root so the
// CSS override wins even when MudBlazor's runtime toggle doesn't propagate
// the class down to us (we've seen it stay on <html> at load but go missing
// after runtime toggles, leaving the island stuck in light mode).
const darkObservers = new Map<string, () => void>();

function isDarkActive(): boolean {
  if (document.documentElement.classList.contains('mud-theme-dark')) return true;
  if (document.body?.classList.contains('mud-theme-dark')) return true;
  try {
    if (localStorage.getItem('darkMode') === 'true') return true;
  } catch { /* storage blocked */ }
  return false;
}

function syncDarkClass(el: HTMLElement) {
  el.classList.toggle('adm-dark-mode', isDarkActive());
}

function installDarkObserver(rootId: string, el: HTMLElement) {
  syncDarkClass(el);
  const htmlObs = new MutationObserver(() => syncDarkClass(el));
  htmlObs.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
  const bodyObs = new MutationObserver(() => syncDarkClass(el));
  if (document.body) bodyObs.observe(document.body, { attributes: true, attributeFilter: ['class'] });
  const storageHandler = (e: StorageEvent) => {
    if (e.key === 'darkMode') syncDarkClass(el);
  };
  window.addEventListener('storage', storageHandler);
  darkObservers.set(rootId, () => {
    htmlObs.disconnect();
    bodyObs.disconnect();
    window.removeEventListener('storage', storageHandler);
  });
}

/**
 * A mount is healthy only if the CURRENT root element is the exact node we
 * mounted into, it's still in the document, and it still holds our rendered
 * children. Blazor's .NET 10 circuit resume REPLACES the root node and re-runs
 * the host component's lifecycle — a fresh firstRender re-calls mount().
 * Comparing the live node to the one we mounted into lets the re-invoked
 * mount() rebuild instead of no-op.
 */
function isHealthy(rootId: string): boolean {
  const inst = instances.get(rootId);
  if (!inst) return false;
  const el = document.getElementById(rootId);
  return el != null && el === inst.el && el.isConnected && el.childElementCount > 0;
}

function mountRoot(rootId: string) {
  desiredRoots.add(rootId);
  if (isHealthy(rootId)) return;
  if (instances.has(rootId)) destroyRoot(rootId, /* internal */ true);
  const el = document.getElementById(rootId) as HTMLElement | null;
  if (!el) {
    console.warn('[admin-island] root element not found:', rootId);
    return;
  }
  installDarkObserver(rootId, el);
  const ctx = readContext(el);
  const app = svelteMount(componentForView(ctx.view), { target: el, props: { ctx } });
  instances.set(rootId, { app, el });
  ensureHealObserver();
}

function destroyRoot(rootId: string, internal = false) {
  const inst = instances.get(rootId);
  if (!inst) {
    if (!internal) desiredRoots.delete(rootId);
    return;
  }
  // Blazor's circuit resume disposes the OLD page component AFTER re-initialising
  // the new one (whose firstRender re-calls mount()). Ignore an external destroy
  // while the current mount is healthy; a genuine navigation-away leaves no healthy
  // root, so real teardown still runs then. `internal` (self-heal) always proceeds.
  if (!internal && isHealthy(rootId)) return;
  if (!internal) desiredRoots.delete(rootId);
  unmount(inst.app);
  instances.delete(rootId);
  const disposer = darkObservers.get(rootId);
  if (disposer) {
    disposer();
    darkObservers.delete(rootId);
  }
}

window.AdminIsland = { mount: mountRoot, destroy: destroyRoot };

// ── Resilience: self-heal after a Blazor circuit resume ────────────────────
// Mobile browsers background the tab on an app/tab switch; Blazor Server's .NET
// 10 circuit pause/resume then reconciles the DOM on return and can REPLACE the
// island root. We watch a STABLE anchor (document.body) and rebuild any desired
// root that has gone unhealthy. `healing` guards re-entrancy; once the root is
// healthy again the next pass no-ops, so it can't loop.
let healObserver: MutationObserver | null = null;
let healing = false;
function runHeal() {
  if (healing) return;
  healing = true;
  try {
    for (const rootId of desiredRoots) {
      if (document.getElementById(rootId) && !isHealthy(rootId)) mountRoot(rootId);
    }
  } finally {
    healing = false;
  }
}
function ensureHealObserver() {
  if (healObserver || !document.body) return;
  healObserver = new MutationObserver(runHeal);
  healObserver.observe(document.body, { childList: true, subtree: true });
}

// bfcache restore re-attaches the frozen DOM without any mutation, so the body
// observer won't fire — re-assert directly on pageshow as a cheap belt.
window.addEventListener('pageshow', () => {
  ensureHealObserver();
  for (const rootId of desiredRoots) {
    if (document.getElementById(rootId) && !isHealthy(rootId)) mountRoot(rootId);
  }
});

// Standalone dev auto-mount: if a known root is already on the page when the
// module loads (Vite's index.html), mount it immediately. In production the
// Razor shell calls window.AdminIsland.mount() explicitly via JS interop.
function autoMountIfReady() {
  for (const rootId of ALL_ROOT_IDS) {
    if (document.getElementById(rootId)) mountRoot(rootId);
  }
}
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', autoMountIfReady);
} else {
  autoMountIfReady();
}
