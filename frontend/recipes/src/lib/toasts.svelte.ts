// ─────────────────────────────────────────────────────────────────────────
// Transient toast notices for the recipes island (mirrors the meal-plan/chores
// toast store). Used for non-alarming 4xx/network surfacing, the "someone
// changed this — refreshed" reconcile notice, and edit-form save outcomes. Kept
// deliberately small: a module-level `$state` list + helpers.
//
// ⚠ Svelte 5 rune rule: we never export a reassigned `$state` rune directly.
// The list lives inside a module-private `$state` object; callers read it via
// the `toasts()` accessor and mutate it only through the exported functions.
// ─────────────────────────────────────────────────────────────────────────

export type ToastKind = 'info' | 'success' | 'error';

/** Optional inline action (e.g. the ingredient delete-with-undo). */
export interface ToastAction {
  label: string;
  onClick: () => void;
}

export interface Toast {
  id: number;
  message: string;
  kind: ToastKind;
  durationMs: number;
  action?: ToastAction;
}

let nextId = 1;
const store = $state<{ items: Toast[] }>({ items: [] });

/** The live toast list (reactive — read inside markup). */
export function toasts(): readonly Toast[] {
  return store.items;
}

/** Push a toast. Auto-dismisses after `durationMs` (default 3.5s). */
export function showToast(toast: {
  message: string;
  kind: ToastKind;
  durationMs?: number;
  action?: ToastAction;
}): number {
  const id = nextId++;
  const full: Toast = {
    id,
    message: toast.message,
    kind: toast.kind,
    durationMs: toast.durationMs ?? 3500,
    action: toast.action,
  };
  store.items = [...store.items, full];
  if (full.durationMs > 0) {
    setTimeout(() => dismissToast(id), full.durationMs);
  }
  return id;
}

export function dismissToast(id: number): void {
  store.items = store.items.filter((t) => t.id !== id);
}
