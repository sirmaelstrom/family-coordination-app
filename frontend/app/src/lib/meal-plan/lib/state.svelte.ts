// ─────────────────────────────────────────────────────────────────────────
// Board store — the single source of truth for the meal-plan island.
//
// One `MealPlanBoardDto` (the current week) is fetched and held here. The
// calendar grid + day list are CLIENT-SIDE groupings of that one payload via
// the per-slot `zones` (rebuilt from `board.entries` outside drags — see the
// gated $effect.pre in App.svelte).
//
// ⚠ Svelte 5 rune rule (global CORRECTION): never `export` a reassigned
// `$state`/`$derived` from a module. We wrap the mutable state in a class
// instance and export the instance — the runes live as `$state`/`$derived`
// fields on the object, which is the supported pattern.
//
// The ops are add + move (drag-to-assign, same week) + servings + remove. All but
// ADD send the entry's xmin token and get a 409 on a stale one (PR #95); add
// creates the row, so it has no prior version. The token for a dialog-driven
// mutation is supplied BY THE CALLER, not re-read here — see setEntryServings for why.
//
// Ordering lives in a Resource (quest 41337b6f). Every write goes through
// mutate(): a load already in flight is retired when the write starts, and no
// load (liveness, reconcile, a week change) is dispatched until the writes
// settle — then ONE load runs. So a same-week poll can no longer revert an
// optimistic move/remove. A 409/other 4xx undoes + reconciles + a calm toast;
// a 5xx/network failure undoes with an error toast and no reconcile. A second
// action on an entry whose write is still pending is skipped (it would send the
// same, now stale, token).
// NO `new Date('YYYY-MM-DD')` anywhere — week stepping goes through
// lib/dates.ts (MN4).
// ─────────────────────────────────────────────────────────────────────────

import type { MealPlanBoardDto, MealPlanEntryDto, MealType, ShellContext } from './types';
import {
  ApiError,
  addEntry,
  getBoard,
  moveEntry,
  removeEntry,
  setEntryServings,
  type AddEntryBody,
} from './api';
import { addWeeks, mondayOf, todayMonday, weekDays } from './dates';
import {
  applyEntryMove,
  buildZones,
  findCrossSlotEntry,
  replaceEntry,
  zoneKey,
  type DragEntry,
} from './board-ops';
import { showToast } from '$lib/shared/toast-store.svelte';
import { Resource, type MutationOutcome } from '$lib/data/resource.svelte';

/** The pending-write key for one entry (a second action on it while pending is skipped). */
const entryKey = (mealPlanId: number, entryId: number): string => `entry:${mealPlanId}:${entryId}`;

const STALE_MEAL = 'That meal just changed — the plan was refreshed. Try again.';
const FAILED = 'Something went wrong. Please try again.';

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
   * Load + write ordering. The Resource applies only the latest load (two loads for the SAME week —
   * liveness vs reconcile vs refresh — can't land out of order) and none that overlaps a pending
   * write. The load carries the week it asked for; commit drops it if the user has since moved on.
   * (With loads deferred behind writes and a week change superseding any older load, that check is
   * defence in depth: a stale-week result can't be current.)
   */
  #resource = new Resource<{ week: string; board: MealPlanBoardDto }>({
    fetch: async () => {
      const week = this.weekStart;
      return { week, board: await getBoard(week) };
    },
    commit: ({ week, board }) => {
      if (this.weekStart !== week) return;
      this.setBoard(board);
    },
    onLoadStart: () => {
      if (this.board == null) this.loading = true; // the spinner is for the first load only
      this.error = null;
    },
    onLoadError: (e) => {
      this.error =
        e instanceof ApiError
          ? `Failed to load the meal plan (HTTP ${e.status}).`
          : e instanceof Error
            ? e.message
            : String(e);
    },
    onLoadIdle: () => {
      this.loading = false;
    },
  });

  // ── Per-slot zone state (drag-to-assign) ─────────────────────────────────
  //
  // svelte-dnd-action multi-zone rule (bit the shopping list): each slot's dnd
  // zone must OWN a stable, mutable items array across a drag — NOT a $derived
  // off `board.entries`. Any cross-zone rebuild mid-drag tears down the dragged
  // DOM node and silently breaks between-slot drops. So `zones` is $state:
  // App.svelte's gated $effect.pre rebuilds it from `board.entries` whenever
  // data changes OUTSIDE a drag; during a drag the consider/finalize handlers
  // below own it and only ever swap ONE zone's array.
  zones = $state<Record<string, DragEntry[]>>({});
  /** True from the first `consider` until `finalize` — gates the zone rebuild. */
  dragActive = $state(false);

  /** One slot's drag rows (a stable empty array is seeded for empty slots). */
  zoneFor(date: string, mealType: MealType): DragEntry[] {
    return this.zones[zoneKey(date, mealType)] ?? [];
  }

  /** Re-derive all zones from the authoritative board. NEVER call mid-drag. */
  rebuildZones(): void {
    this.zones = buildZones(this.board?.entries ?? [], weekDays(this.weekStart), MEAL_ROWS);
  }

  /** Live drag: reflect the library's per-zone items in the zone that fired, nothing else. */
  zoneConsider(date: string, mealType: MealType, items: DragEntry[]): void {
    this.dragActive = true;
    this.zones[zoneKey(date, mealType)] = items;
  }

  /**
   * Drop: commit the fired zone's items, then — only in the DESTINATION zone,
   * detected by a row still carrying its pre-drag slot — persist the move. The
   * optimistic `board.entries` update happens synchronously inside `moveEntry`
   * BEFORE the drag gate is released, so the rebuild reconciles to the moved
   * state (no snap-back frame). The source zone's finalize (and a same-slot
   * reorder, which has nothing to persist) just releases the gate.
   */
  zoneFinalize(date: string, mealType: MealType, items: DragEntry[]): Promise<void> {
    this.zones[zoneKey(date, mealType)] = items;
    const moved = findCrossSlotEntry(items, date, mealType);
    const persist = moved
      // moved.version is the token the dragged CARD carried when the drag began. Board loads are not
      // gated on dragActive (only zone rebuilds are), so a liveness poll landing mid-drag can replace
      // board.entries — re-reading the token at drop would submit one the user never saw. Same guarantee
      // the dialogs get, just over a much shorter window.
      ? this.moveEntry(moved.mealPlanId, moved.entryId, date, mealType, moved.version)
      : Promise.resolve();
    this.dragActive = false;
    return persist;
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
    // Ordering is the Resource's: a superseded load (an older week, or an older load for the SAME
    // week) never commits or surfaces its error, and only the current load clears the spinner. While a
    // write is pending this dispatches nothing and the load runs once the writes settle — for the week
    // that is current THEN (a week change mid-write loads the new week, once).
    await this.#resource.load();
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
   * returned authoritative entry into `board.entries` (no temp id). A 409/other
   * 4xx reconciles (re-GET the week) + a calm toast; a 5xx/network failure toasts.
   */
  async addEntry(body: AddEntryBody): Promise<void> {
    if (!this.board) return;
    // Capture the week the add targets. If the user navigates to another week while the POST is in flight,
    // we must NOT merge this (off-week) entry into the now-current board (council R1). It is persisted
    // server-side and will appear when its week is viewed. No key: two adds are legitimately concurrent.
    const targetWeek = this.weekStart;
    const outcome = await this.#resource.mutate({
      call: () => addEntry(body),
      commit: (created) => {
        if (!this.board || this.weekStart !== targetWeek) return;
        // The POST may have GetOrCreated the week's plan, so the board's
        // mealPlanId could have been null — adopt the entry's plan id.
        if (this.board.mealPlanId == null) {
          this.board.mealPlanId = created.mealPlanId;
        }
        this.board.entries = [...this.board.entries, created];
      },
    });
    this.notify(outcome, targetWeek, {
      conflict: "Couldn't add that meal — the plan was refreshed.",
      rejected: "Couldn't add that meal — the plan was refreshed.",
      failed: "Couldn't add that meal right now.",
    });
  }

  /**
   * The failure toast for a write's outcome — only while the user is still on the week the write was
   * for (a failure on a week they have left is moot; its reconcile already shows the current week).
   * The 409 copy is chosen by the outcome, never a raw status compare.
   */
  private notify(
    outcome: MutationOutcome,
    targetWeek: string,
    copy: { conflict: string; rejected: string; failed: string },
  ): void {
    if (outcome === 'ok' || outcome === 'skipped' || this.weekStart !== targetWeek) return;
    showToast({ message: copy[outcome], kind: outcome === 'failed' ? 'error' : 'info' });
  }

  /**
   * Move an entry to another slot in the SAME week (drag-to-assign). Optimistic
   * re-slot, then PATCH; the server echo is merged back (authoritative
   * UpdatedAt/UpdatedBy). Any failure reverts the optimistic move; a 409 or
   * other 4xx (cross-week, duplicate-in-slot, entry gone — a clean 404 since
   * PR #90) also reconciles + a calm toast; network/5xx → an error toast. Same
   * targetWeek guard as addEntry, on the commit AND the undo.
   */
  async moveEntry(
    mealPlanId: number,
    entryId: number,
    date: string,
    mealType: MealType,
    version: number,
  ): Promise<void> {
    if (!this.board) return;
    const original = this.board.entries.find(
      (e) => e.mealPlanId === mealPlanId && e.entryId === entryId,
    );
    if (!original) return;
    const fromDate = original.date;
    const fromMeal = original.mealType;
    if (fromDate === date && fromMeal === mealType) return;

    const targetWeek = this.weekStart;
    const outcome = await this.#resource.mutate({
      key: entryKey(mealPlanId, entryId),
      // Optimistic re-slot. mutate() runs this synchronously, before its first await — zoneFinalize relies
      // on it landing before the drag gate is released.
      optimistic: () => {
        if (!this.board) return;
        this.board.entries = applyEntryMove(this.board.entries, mealPlanId, entryId, date, mealType);
        // Undo: the same transform aimed back at the original slot — only on the week it was made on
        // (restoring into another week's board would move a foreign entry).
        return () => {
          if (!this.board || this.weekStart !== targetWeek) return;
          this.board.entries = applyEntryMove(this.board.entries, mealPlanId, entryId, fromDate, fromMeal);
        };
      },
      // Caller-supplied token (the dragged card's), NOT original.version — see zoneFinalize.
      call: () => moveEntry(mealPlanId, entryId, { date, mealType, version }),
      commit: (updated) => {
        // The user stepped to another week while the PATCH was in flight — this
        // board is no longer the one we moved on; the new week's GET is truth.
        if (!this.board || this.weekStart !== targetWeek) return;
        this.board.entries = replaceEntry(this.board.entries, updated);
      },
    });
    // A 409 means the token we sent was stale — usually another member. (A second action on the same entry
    // before the first response lands used to send the same stale token too; the pending key now skips
    // it.) The copy says what we know — the meal changed — and the reconcile shows server truth.
    this.notify(outcome, targetWeek, {
      conflict: STALE_MEAL,
      rejected: "Couldn't move that meal — the plan was refreshed.",
      failed: FAILED,
    });
  }

  /**
   * Set how many people a planned meal is being cooked for; `null` clears the
   * override back to the recipe as written. Shopping-list generation scales that
   * entry's ingredients by `servings / recipe.servings`.
   *
   * NOT optimistic, unlike move/remove: this fires from a dialog the user just
   * dismissed rather than from a drag, so there is no gesture to keep in sync and
   * the server's projected entry is simply the truth. Same week-changed guard as
   * the others — a response for a board we have already navigated away from is
   * dropped, not applied.
   */
  async setEntryServings(
    mealPlanId: number,
    entryId: number,
    servings: number | null,
    version: number,
  ): Promise<void> {
    if (!this.board) return;
    const targetWeek = this.weekStart;
    const outcome = await this.#resource.mutate({
      key: entryKey(mealPlanId, entryId),
      // The token comes from the CALLER — the entry as it was when the dialog opened — not from a fresh
      // lookup here. A ~20s liveness poll can replace the board entry while the dialog sits open, and
      // re-sampling would submit a version the user never saw, quietly overwriting whatever the other
      // member just changed. That is exactly the write this feature exists to refuse.
      call: () => setEntryServings(mealPlanId, entryId, servings, version),
      commit: (updated) => {
        if (!this.board || this.weekStart !== targetWeek) return;
        this.board.entries = replaceEntry(this.board.entries, updated);
      },
    });
    this.notify(outcome, targetWeek, {
      conflict: STALE_MEAL,
      rejected: "Couldn't change the servings — the plan was refreshed.",
      failed: FAILED,
    });
  }

  /**
   * Remove an entry — optimistic splice, then DELETE. Any failure puts it back;
   * a 409/other 4xx also reconciles + a calm toast. A missing entry (already
   * removed by someone else) → 404 → the refetch shows the true state.
   */
  async removeEntry(mealPlanId: number, entryId: number, version: number): Promise<void> {
    if (!this.board) return;
    const idx = this.board.entries.findIndex(
      (e) => e.mealPlanId === mealPlanId && e.entryId === entryId,
    );
    if (idx < 0) return;
    const removed = this.board.entries[idx];
    const targetWeek = this.weekStart;
    const isThis = (x: MealPlanEntryDto) => x.mealPlanId === mealPlanId && x.entryId === entryId;
    const outcome = await this.#resource.mutate({
      key: entryKey(mealPlanId, entryId),
      optimistic: () => {
        if (!this.board) return;
        this.board.entries = this.board.entries.filter((e) => !isThis(e));
        // Undo: put it back AT ITS ORIGINAL INDEX (council R1 — appending would reorder the slot's entries),
        // only on the week it was removed from (restoring into another week would splice a foreign entry
        // into the visible week) and only if it isn't already there.
        return () => {
          if (!this.board || this.weekStart !== targetWeek) return;
          if (this.board.entries.some(isThis)) return;
          const restored = [...this.board.entries];
          restored.splice(Math.min(idx, restored.length), 0, removed);
          this.board.entries = restored;
        };
      },
      // Caller-supplied token, for the same reason as setEntryServings — the confirm dialog can sit open
      // across a liveness poll.
      call: () => removeEntry(mealPlanId, entryId, version),
    });
    // A 409 means someone else changed this entry after we read it, so say so rather than the generic line.
    // Any other 4xx (a missing entry is a clean 404 since PR #90) reconciles to truth.
    this.notify(outcome, targetWeek, {
      conflict: STALE_MEAL,
      rejected: 'That meal changed — the plan was refreshed.',
      failed: FAILED,
    });
  }
}

/**
 * The single shared store instance. Components import this and read its
 * `$state`/`$derived` fields reactively. We export the INSTANCE (not the runes
 * themselves) so the rune-export rule is respected.
 */
export const mealPlanStore = new MealPlanStore();
