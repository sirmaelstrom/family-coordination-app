import type { DashboardDto, DashboardMealDto, MealType, ShellContext } from './types';
import { getDashboard } from './api';

/** A meal-type group for the Today's Meals card (preserves the server's MealType ordering). */
export interface MealGroup {
  mealType: MealType;
  meals: DashboardMealDto[];
}

/**
 * The dashboard island store (Svelte-5 rune rule: export the class INSTANCE, never a
 * reassigned `$state`). Read-only — one aggregate GET, then liveness/refocus reconcile.
 * No writes ⇒ no optimistic/draft machinery; the one race guard is `#seq` (a slow earlier
 * load that resolves after a newer one is dropped).
 */
export class DashboardStore {
  data = $state<DashboardDto | null>(null);
  loading = $state(true);
  error = $state<string | null>(null);
  currentUserId = $state(0);

  // Monotonic load guard — only the newest load's response is applied.
  #seq = 0;

  /** "needs attention" = overdue + due-today (display logic, not a wire field). */
  choreAttention = $derived(this.data ? this.data.chores.overdue + this.data.chores.dueToday : 0);

  /** Shopping progress percent (0 when nothing to buy). */
  shoppingProgress = $derived.by(() => {
    const s = this.data?.shopping;
    if (!s || s.total <= 0) return 0;
    return Math.round((s.checked / s.total) * 100);
  });

  /** Today's meals grouped by type, preserving the server's MealType order (parity: GroupBy(MealType)). */
  mealsByType = $derived.by(() => {
    const groups: MealGroup[] = [];
    for (const meal of this.data?.todaysMeals ?? []) {
      let g = groups.find((x) => x.mealType === meal.mealType);
      if (!g) {
        g = { mealType: meal.mealType, meals: [] };
        groups.push(g);
      }
      g.meals.push(meal);
    }
    return groups;
  });

  init(ctx: ShellContext): void {
    this.currentUserId = ctx.userId;
  }

  /** Load the aggregate. Guarded so an out-of-order (stale) response is ignored. */
  async load(): Promise<void> {
    const seq = ++this.#seq;
    try {
      const d = await getDashboard();
      if (seq !== this.#seq) return; // superseded by a newer load
      this.data = d;
      this.error = null;
    } catch (e) {
      if (seq !== this.#seq) return;
      // Keep the last good data (no blank-out) and surface a calm error.
      this.error = e instanceof Error ? e.message : 'Failed to load the dashboard.';
    } finally {
      if (seq === this.#seq) this.loading = false;
    }
  }

  /** Liveness tick + error reconcile — just re-load; the seq guard drops stale ticks. */
  reconcile(): void {
    void this.load();
  }
}

export const dashboardStore = new DashboardStore();
