// ─────────────────────────────────────────────────────────────────────────
// Dark-mode store for the SvelteKit shell.
//
// Source of truth for the class `mud-theme-dark` on <html>. Persists to
// localStorage['darkMode'] AND the `darkMode` cookie (via setPrefCookie, which
// the app.html pre-paint script defines) so the server can seed the preference
// and the pre-paint script avoids a flash on the next load.
//
// ⚠ The class name `mud-theme-dark` is load-bearing during strangler
// coexistence — not-yet-migrated islands detect dark mode by this EXACT class.
// Do NOT rename it until the old islands are gone (WP-12).
// ─────────────────────────────────────────────────────────────────────────

declare global {
  interface Window {
    setPrefCookie?: (name: string, value: string) => void;
  }
}

const DARK_CLASS = 'mud-theme-dark';
const DARK_BG = '#121212';
const STORAGE_KEY = 'darkMode';

function writePref(name: string, value: string): void {
  if (typeof document === 'undefined') return;
  // Prefer the shared helper from app.html so cookie attributes stay in lockstep
  // with the Blazor app; fall back to writing the cookie directly if absent.
  if (typeof window !== 'undefined' && window.setPrefCookie) {
    window.setPrefCookie(name, value);
  } else {
    document.cookie = `${name}=${value}; path=/; max-age=31536000; SameSite=Lax`;
  }
}

class ThemeStore {
  #dark = $state(false);

  /** Whether dark mode is currently active (reactive). */
  get dark(): boolean {
    return this.#dark;
  }

  /**
   * Sync store state to whatever the pre-paint script already applied. Call once
   * from the root layout's onMount. The pre-paint script (app.html) has already
   * added/removed `mud-theme-dark` before first paint, so we read from there +
   * localStorage rather than re-deciding (no flash, no double source of truth).
   */
  init(): void {
    if (typeof window === 'undefined') return;
    const stored = localStorage.getItem(STORAGE_KEY);
    this.#dark =
      stored === 'true' ||
      (stored === null && document.documentElement.classList.contains(DARK_CLASS));
    this.#apply();
  }

  toggle(): void {
    this.set(!this.#dark);
  }

  set(value: boolean): void {
    this.#dark = value;
    this.#apply();
    if (typeof window === 'undefined') return;
    localStorage.setItem(STORAGE_KEY, String(value));
    writePref(STORAGE_KEY, String(value));
  }

  #apply(): void {
    if (typeof document === 'undefined') return;
    const el = document.documentElement;
    if (this.#dark) {
      el.classList.add(DARK_CLASS);
      el.style.backgroundColor = DARK_BG;
    } else {
      el.classList.remove(DARK_CLASS);
      el.style.backgroundColor = '';
    }
  }
}

/** The canonical, app-wide theme singleton. */
export const theme = new ThemeStore();
