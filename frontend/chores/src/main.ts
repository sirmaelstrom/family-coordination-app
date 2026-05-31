import { mount as svelteMount, unmount } from 'svelte';
import App from './App.svelte';
import './styles/app.css';
import type { ShellContext } from './lib/types';

type MountedApp = ReturnType<typeof svelteMount>;

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

const instances = new Map<string, MountedApp>();

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

function mountRoot(rootId: string) {
  const el = document.getElementById(rootId) as HTMLElement | null;
  if (!el) {
    console.warn('[chores-island] root element not found:', rootId);
    return;
  }
  if (instances.has(rootId)) return;
  installDarkObserver(rootId, el);
  const app = svelteMount(App, { target: el, props: { ctx: readContext(el) } });
  instances.set(rootId, app);
}

function destroyRoot(rootId: string) {
  const app = instances.get(rootId);
  if (!app) return;
  unmount(app);
  instances.delete(rootId);
  const disposer = darkObservers.get(rootId);
  if (disposer) {
    disposer();
    darkObservers.delete(rootId);
  }
}

window.ChoresIsland = { mount: mountRoot, destroy: destroyRoot };

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
