// ─────────────────────────────────────────────────────────────────────────
// Canonical toast store for the SvelteKit shell — the ONE copy every island
// route imports (orchestrator M8/P3). Module-level singleton: one toast region
// for the whole app.
//
// This is the UNION of every island's toast API, so it's drop-in for all callers:
//   • shopping-list style:  showToast({ message, kind, actionLabel, onAction })
//                           + triggerToastAction(id)
//   • admin/recipes/settings style: showToast({ message, kind, action: { label, onClick } })
//   • chores/connections/dashboard/meal-plan style: showToast({ message, kind, durationMs })
// All action shapes normalise to the internal `action` field.
//
// ⚠ Svelte 5 rune rule: the list lives inside a module-private $state object;
// callers read it via the `toasts()` accessor and mutate only through the
// exported functions (never a re-exported reassigned $state).
// ─────────────────────────────────────────────────────────────────────────

export type ToastKind = 'info' | 'success' | 'error';

/** Normalised inline action (e.g. undo). */
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

/** The union of every caller's showToast argument shape. */
export interface ShowToastInput {
  message: string;
  kind: ToastKind;
  durationMs?: number;
  /** admin/recipes/settings convention. */
  action?: ToastAction;
  /** shopping-list convention (normalised into `action`). */
  actionLabel?: string;
  /** shopping-list convention (normalised into `action`). */
  onAction?: () => void;
}

const DEFAULT_DURATION_MS = 3500;

let nextId = 1;
const store = $state<{ items: Toast[] }>({ items: [] });

/** The live toast list (reactive — read inside markup). */
export function toasts(): readonly Toast[] {
  return store.items;
}

/** Push a toast. Auto-dismisses after `durationMs` (default 3.5s; 0 = sticky). */
export function showToast(toast: ShowToastInput): number {
  const id = nextId++;
  const action: ToastAction | undefined =
    toast.action ??
    (toast.actionLabel
      ? { label: toast.actionLabel, onClick: toast.onAction ?? (() => {}) }
      : undefined);

  const full: Toast = {
    id,
    message: toast.message,
    kind: toast.kind,
    durationMs: toast.durationMs ?? DEFAULT_DURATION_MS,
    action,
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

/** Run a toast's action then dismiss it (shopping-list convention). */
export function triggerToastAction(id: number): void {
  const t = store.items.find((x) => x.id === id);
  if (!t) return;
  try {
    t.action?.onClick();
  } finally {
    dismissToast(id);
  }
}
