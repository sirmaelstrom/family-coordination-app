export interface Toast {
  id: number;
  message: string;
  kind: 'info' | 'success' | 'error';
  actionLabel?: string;
  onAction?: () => void;
  durationMs: number;
}

let nextId = 1;
const state = $state<{ items: Toast[] }>({ items: [] });

export function toasts(): readonly Toast[] {
  return state.items;
}

export function showToast(toast: Omit<Toast, 'id' | 'durationMs'> & { durationMs?: number }) {
  const id = nextId++;
  const full: Toast = {
    id,
    message: toast.message,
    kind: toast.kind,
    actionLabel: toast.actionLabel,
    onAction: toast.onAction,
    durationMs: toast.durationMs ?? 3000,
  };
  state.items = [...state.items, full];
  if (full.durationMs > 0) {
    setTimeout(() => dismissToast(id), full.durationMs);
  }
  return id;
}

export function dismissToast(id: number) {
  state.items = state.items.filter((t) => t.id !== id);
}

export function triggerToastAction(id: number) {
  const t = state.items.find((t) => t.id === id);
  if (!t) return;
  try {
    t.onAction?.();
  } finally {
    dismissToast(id);
  }
}
