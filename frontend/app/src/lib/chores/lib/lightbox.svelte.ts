// ─────────────────────────────────────────────────────────────────────────
// Photo lightbox (v1.2) — a single shared tap-to-enlarge overlay for the
// chores island. Any surface (the ChoreCard leading thumbnail, the room
// drill-in hero) calls showPhoto(src, alt) to enlarge a photo; one
// <PhotoLightbox> mounted in App.svelte renders it as a native <dialog>
// (showModal() + ::backdrop — Esc closes natively, backdrop click dismisses).
//
// Mirrors the toasts store: a module-private `$state` object read via the
// `lightbox()` accessor and mutated only through the exported helpers. We never
// export a reassigned `$state` rune directly (Svelte 5 rune rule / CORRECTION).
// ─────────────────────────────────────────────────────────────────────────

export interface LightboxState {
  /** The photo URL shown full-size, or null when the lightbox is closed. */
  src: string | null;
  /** Alt text for the enlarged image. */
  alt: string;
}

const store = $state<LightboxState>({ src: null, alt: '' });

/** The live lightbox state (reactive — read inside markup / effects). */
export function lightbox(): Readonly<LightboxState> {
  return store;
}

/** Open the lightbox on a photo. No-op for a blank/missing src. */
export function showPhoto(src: string | null | undefined, alt = ''): void {
  if (!src) return;
  store.src = src;
  store.alt = alt;
}

/** Close the lightbox. */
export function closePhoto(): void {
  store.src = null;
}
