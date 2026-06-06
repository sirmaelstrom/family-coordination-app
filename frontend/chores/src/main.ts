import { mount as svelteMount, unmount } from 'svelte';
import App from './App.svelte';
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
// tags in that output are NOT executed by the browser. The Razor shell
// (Chores.razor) uses IJSRuntime.InvokeAsync("import", ...) to load this
// module, then calls the global mounter below. Exposing a named entry on
// window is the simplest way to survive enhanced-nav round-trips.
declare global {
  interface Window {
    ChoresIsland?: {
      mount(rootId: string): void;
      destroy(rootId: string): void;
    };
  }
}

const ROOT_ID = 'chores-root';

const instances = new Map<string, Instance>();

// Roots the Razor shell has asked us to mount. We re-assert these when the page
// is restored so a Blazor circuit resume / enhanced-nav merge that wiped our
// content can't leave the island permanently blank (see reassertMounts below).
const desiredRoots = new Set<string>();

function readContext(el: HTMLElement): ShellContext {
  const householdId = Number(el.dataset.householdId ?? '0');
  const userId = Number(el.dataset.userId ?? '0');
  const userName = el.dataset.userName ?? '';
  if (!householdId || !userId) {
    throw new Error('chores: missing data-household-id / data-user-id');
  }
  return { householdId, userId, userName };
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
  el.classList.toggle('ch-dark-mode', isDarkActive());
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
 * children. Blazor's .NET 10 circuit resume (the path that fires on a mobile
 * background→return) REPLACES the root node and re-runs the host component's
 * lifecycle — a fresh firstRender re-calls mount(). The old mount-once guard
 * (`instances.has`) refused to re-mount into that new node, leaving the board
 * blank until a full reload. Comparing the live node to the one we mounted into
 * lets the re-invoked mount() rebuild instead of no-op.
 */
function isHealthy(rootId: string): boolean {
  const inst = instances.get(rootId);
  if (!inst) return false;
  const el = document.getElementById(rootId);
  return el != null && el === inst.el && el.isConnected && el.childElementCount > 0;
}

function mountRoot(rootId: string) {
  desiredRoots.add(rootId);
  // Already mounted AND still intact — nothing to do.
  if (isHealthy(rootId)) return;
  // A stale prior mount (root emptied / replaced / detached): tear it down first
  // so we don't leak the Svelte instance or its observers before remounting.
  if (instances.has(rootId)) destroyRoot(rootId, /* internal */ true);
  const el = document.getElementById(rootId) as HTMLElement | null;
  if (!el) {
    console.warn('[chores-island] root element not found:', rootId);
    return;
  }
  installDarkObserver(rootId, el);
  const app = svelteMount(App, { target: el, props: { ctx: readContext(el) } });
  instances.set(rootId, { app, el });
  ensureHealObserver();
}

function destroyRoot(rootId: string, internal = false) {
  const inst = instances.get(rootId);
  if (!inst) {
    if (!internal) desiredRoots.delete(rootId);
    return;
  }
  // Blazor's circuit resume disposes the OLD page component AFTER it has already
  // re-initialised the new one — whose firstRender re-calls mount(), which we use
  // to rebuild into the freshly-replaced root. That trailing destroy() targets a
  // SUPERSEDED component and would otherwise tear our live island back down.
  // Ignore an external destroy while the current mount is healthy; a genuine
  // navigation-away leaves no healthy root (the page is gone), so real teardown
  // still runs then. `internal` is our own self-heal replacing a stale mount — it
  // must always proceed.
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

window.ChoresIsland = { mount: mountRoot, destroy: destroyRoot };

// ── Resilience: self-heal after a Blazor circuit resume ────────────────────
// Mobile browsers background the tab on an app/tab switch; Blazor Server's .NET
// 10 circuit pause/resume then reconciles the DOM on return and can REPLACE the
// island root — sometimes re-invoking our mount() (handled in mountRoot), and
// sometimes NOT (observed: nondeterministic). Either way the swap is a DOM
// mutation, so we watch a STABLE anchor (document.body — Blazor replaces a chunk
// of the island's ancestors, so observing the root or its parent misses it) and
// rebuild any desired root that has gone unhealthy.
//
// The heal runs SYNCHRONOUSLY in the observer microtask — NOT via
// requestAnimationFrame, which is paused while the tab is hidden (the wipe can
// land a beat after the tab is technically still settling). `healing` guards
// re-entrancy (mountRoot mutates the DOM, which re-queues this callback); once
// the root is healthy again the next pass no-ops, so it can't loop. The cost on
// every unrelated DOM change is one getElementById + isHealthy per desired root
// (normally one), which is negligible.
let healObserver: MutationObserver | null = null;
let healing = false;
function runHeal() {
  if (healing) return;
  healing = true;
  try {
    for (const rootId of desiredRoots) {
      // Only rebuild when the root EXISTS but is stale/empty. A missing root is
      // a navigation-away (or mid-reconcile) — not ours to remount; the next
      // mutation re-checks once the fresh root lands.
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

// Standalone dev auto-mount: if the root is already on the page when the
// module loads (Vite's index.html), mount immediately. In production the
// Razor shell calls window.ChoresIsland.mount() explicitly via JS interop.
function autoMountIfReady() {
  const el = document.getElementById(ROOT_ID);
  if (el) mountRoot(ROOT_ID);
}
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', autoMountIfReady);
} else {
  autoMountIfReady();
}
