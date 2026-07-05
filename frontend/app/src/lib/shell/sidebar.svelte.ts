// ─────────────────────────────────────────────────────────────────────────
// Collapsed/expanded state for the desktop side nav (the mini-rail toggle).
//
// Parity restore: the pre-flip Blazor shell had a MudDrawer Variant="Mini"
// with a hamburger in the app bar (MainLayout.razor ToggleDrawer). The
// de-Blazor flip replaced it with a fixed, always-open 240px rail and dropped
// the toggle. This store brings the collapse back.
//
// Persisted to localStorage['navCollapsed'] so the choice sticks across loads.
// The app is a client-only SPA (ssr=false), so we can read localStorage at
// construction; the `typeof` guard keeps the adapter-static build step (where
// window/localStorage are absent) from throwing.
// ─────────────────────────────────────────────────────────────────────────

const STORAGE_KEY = 'navCollapsed';

class SidebarStore {
  #collapsed = $state(false);

  constructor() {
    if (typeof localStorage !== 'undefined') {
      this.#collapsed = localStorage.getItem(STORAGE_KEY) === 'true';
    }
  }

  /** Whether the desktop side nav is collapsed to the icon-only mini rail. */
  get collapsed(): boolean {
    return this.#collapsed;
  }

  toggle(): void {
    this.set(!this.#collapsed);
  }

  set(value: boolean): void {
    this.#collapsed = value;
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem(STORAGE_KEY, String(value));
    }
  }
}

/** The canonical, app-wide side-nav collapse singleton. */
export const sidebar = new SidebarStore();
