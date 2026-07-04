// ─────────────────────────────────────────────────────────────────────────
// Board store — the single source of truth for the meal-plan island.
//
// One `MealPlanBoardDto` (the current week) is fetched and held here. The
// calendar grid + day list are CLIENT-SIDE groupings of that one payload via
// `entriesByDayMeal`.
//
// ⚠ Svelte 5 rune rule (global CORRECTION): never `export` a reassigned
// `$state`/`$derived` from a module. We wrap the mutable state in a class
// instance and export the instance — the runes live as `$state`/`$derived`
// fields on the object, which is the supported pattern.
//
// Parity-first ⇒ VERSIONLESS / last-write-wins. The only ops are add + remove;
// there is no xmin token, no 409 branch. On any 4xx/network error we reconcile
// (re-GET the current week) + surface a calm toast. NO `new Date('YYYY-MM-DD')`
// anywhere — week stepping goes through lib/dates.ts (MN4).
// ─────────────────────────────────────────────────────────────────────────

import type { MealPlanBoardDto, MealPlanEntryDto, MealType, ShellContext } from './types';
import { ApiError, addEntry, getBoard, removeEntry, type AddEntryBody } from './api';
import { addWeeks, mondayOf, todayMonday } from './dates';
import { showToast } from '$lib/shared/toast-store.svelte';

/** The board's three rendered meal rows (Snack is implicit — only shown if data exists). */
export const MEAL_ROWS: MealType[] = ['breakfast', 'lunch', 'dinner'];

class MealPlanStore {
  /** The week's Monday "YYYY-MM-DD". Client stepping is display-only — the server re-snaps on GET. */
  weekStart = $state<string>(todayMonday());
  /** The one fetched payload for `weekStart`. null until the first load resolves. */
  board = $state<MealPlanBoardDto | null>(null);
  loading = $state(true);
  /** Human-readable error message; null when healthy. */
  error = $state<string | null>(null);
  /** This household member's userId — set once on init from the shell context. */
  currentUserId = $state(0);

  /**
   * The board refetch hook, wired by App.svelte (`store.setRefresh(loadBoard)`).
   * Shared by liveness AND the error-reconcile path so a post-mutation refetch
   * reuses the exact same loader.
   */
  private refresh: (() => Promise<void>) | null = null;

  /**
   * Slot lookup: `${date}|${mealType}` → the entries in that slot. Empty map
   * until the board loads. A slot with no entries simply isn't a key.
   */
  entriesByDayMeal = $derived.by(() => {
    const map = new Map<string, MealPlanEntryDto[]>();
    for (const e of this.board?.entries ?? []) {
      const key = `${e.date}|${e.mealType}`;
      const bucket = map.get(key);
      if (bucket) bucket.push(e);
      else map.set(key, [e]);
    }
    return map;
  });

  /** Entries for one slot (empty array when none). */
  entriesFor(date: string, mealType: MealType): MealPlanEntryDto[] {
    return this.entriesByDayMeal.get(`${date}|${mealType}`) ?? [];
  }

  /** Count of all entries on a given day (for the mobile day-list "N meals" label). */
  dayCount(date: string): number {
    return (this.board?.entries ?? []).filter((e) => e.date === date).length;
  }

  /** Seed the viewing user's id (drives "you"-style affordances if ever needed). */
  init(ctx: ShellContext): void {
    this.currentUserId = ctx.userId;
  }

  /** Wire the board refetch (App.svelte's loader). Used for liveness + error reconcile. */
  setRefresh(refresh: () => Promise<void>): void {
    this.refresh = refresh;
  }

  /** Replace the board payload (the server echoes the snapped Monday — adopt it). */
  setBoard(next: MealPlanBoardDto): void {
    this.board = next;
    // Adopt the server's authoritative Monday so the label can't drift from the
    // data (e.g. if the client and server disagreed on the week boundary).
    this.weekStart = next.weekStartDate;
    this.error = null;
  }

  /** Re-GET the current week. Shared by liveness + error reconcile. */
  async reconcile(): Promise<void> {
    if (this.refresh) await this.refresh();
  }

  /**
   * Fetch the board for the CURRENT `weekStart`. App.svelte wires this as the
   * loader (and as the liveness/reconcile refresh). Keeps the spinner only for
   * the first load; liveness refreshes are silent.
   */
  async loadBoard(): Promise<void> {
    try {
      if (this.board == null) this.loading = true;
      this.error = null;
      this.setBoard(await getBoard(this.weekStart));
    } catch (e) {
      if (e instanceof ApiError) {
        this.error = `Failed to load the meal plan (HTTP ${e.status}).`;
      } else {
        this.error = e instanceof Error ? e.message : String(e);
      }
    } finally {
      this.loading = false;
    }
  }

  /**
   * Step the week. A number = relative weeks (−1 prev / +1 next); a string =
   * an explicit target date (snapped to its Monday — used by "jump to today").
   * Recomputes `weekStart` via dates.ts then refetches.
   */
  async changeWeek(deltaOrMonday: number | string): Promise<void> {
    this.weekStart =
      typeof deltaOrMonday === 'number'
        ? addWeeks(this.weekStart, deltaOrMonday)
        : mondayOf(deltaOrMonday);
    await this.loadBoard();
  }

  /**
   * Add a meal to a slot. Add is infrequent, so we AWAIT the POST then merge the
   * returned authoritative entry into `board.entries` (no temp id). On any
   * 4xx/network error: reconcile (re-GET the week) + a calm toast.
   */
  async addEntry(body: AddEntryBody): Promise<void> {
    if (!this.board) return;
    // Capture the week the add targets. If the user navigates to another week while the POST is in flight,
    // we must NOT merge this (off-week) entry into the now-current board (council R1). It is persisted
    // server-side and will appear when its week is viewed.
    const targetWeek = this.weekStart;
    try {
      const created = await addEntry(body);
      if (!this.board || this.weekStart !== targetWeek) return;
      // The POST may have GetOrCreated the week's plan, so the board's
      // mealPlanId could have been null — adopt the entry's plan id.
      if (this.board.mealPlanId == null) {
        this.board.mealPlanId = created.mealPlanId;
      }
      this.board.entries = [...this.board.entries, created];
    } catch (e) {
      await this.reconcile();
      const msg =
        e instanceof ApiError
          ? "Couldn't add that meal — the plan was refreshed."
          : "Couldn't add that meal right now.";
      showToast({ message: msg, kind: 'info' });
    }
  }

  /**
   * Remove an entry — optimistic splice, then DELETE. On any error reconcile +
   * calm toast. A missing entry (already removed by someone else) → 404/empty-400
   * → the refetch shows the true state.
   */
  async removeEntry(mealPlanId: number, entryId: number): Promise<void> {
    if (!this.board) return;
    const idx = this.board.entries.findIndex(
      (e) => e.mealPlanId === mealPlanId && e.entryId === entryId,
    );
    if (idx < 0) return;
    const removed = this.board.entries[idx];
    // Optimistic splice.
    this.board.entries = this.board.entries.filter(
      (e) => !(e.mealPlanId === mealPlanId && e.entryId === entryId),
    );
    try {
      await removeEntry(mealPlanId, entryId);
    } catch (e) {
      if (e instanceof ApiError) {
        // Any 4xx (incl. the empty-400-from-404 quirk) — resync to truth.
        await this.reconcile();
        showToast({ message: 'That meal changed — the plan was refreshed.', kind: 'info' });
      } else {
        // Network/5xx: the refetch may not have restored it; put it back AT ITS ORIGINAL INDEX (council R1 —
        // appending would reorder the slot's entries on a failed delete).
        if (
          this.board &&
          !this.board.entries.some(
            (x) => x.mealPlanId === mealPlanId && x.entryId === entryId,
          )
        ) {
          const restored = [...this.board.entries];
          restored.splice(Math.min(idx, restored.length), 0, removed);
          this.board.entries = restored;
        }
        showToast({ message: 'Something went wrong. Please try again.', kind: 'error' });
      }
    }
  }
}

/**
 * The single shared store instance. Components import this and read its
 * `$state`/`$derived` fields reactively. We export the INSTANCE (not the runes
 * themselves) so the rune-export rule is respected.
 */
export const mealPlanStore = new MealPlanStore();
