// ─────────────────────────────────────────────────────────────────────────
// The active shopping list — the state behind shopping-list/App.svelte, moved out
// of the component (spec §5 row 11, A8) so its ordering is unit-testable (quest
// 41337b6f). One instance per App mount (not a singleton).
//
// ONE Resource owns WHEN a response may land, and it loads the list AND the list
// summaries together: every write here changes both (an item write moves the
// summaries' item / unchecked counts; a list write moves the switcher), and the
// old code retired both counters on every write. While ANY write is pending no
// load is dispatched (it is owed and runs once at settle), and a load already in
// flight when a write starts is retired — so no GET that overlaps a write can
// commit, in either response order (the fca#103 residual; the old per-callsite
// `listLoadSeq`/`listsLoadSeq` + retireInFlightLoads() closed only the
// in-flight-at-start and answered-after-the-response orderings).
//
// Writes go through `mutate` (optimistic item check / quantity / favorite; plain
// add / edit / delete / reorder) or `run` (list-level actions that reload). The
// 409 / other-4xx / 5xx-network branch is the Resource's, through the ApiError
// getters — no raw status compare. Item tokens: lib/itemSync.ts rules, unchanged.
// ─────────────────────────────────────────────────────────────────────────

import type { ShoppingListDto, ShoppingListItemDto, ShoppingListSummaryDto } from './types';
import {
  ApiError,
  addItem,
  archiveList,
  clearChecked,
  createList,
  deleteItem,
  generateFromMealPlan,
  getList,
  listLists,
  patchItem,
  regenerateList,
  renameList,
  toggleFavorite,
  updateSortOrders,
  type SortOrderUpdate,
} from './api';
import { adoptItem, adoptReorderVersions } from './itemSync';
import { Resource, type MutationOutcome } from '$lib/data/resource.svelte';
import { showToast } from '$lib/shared/toast-store.svelte';

/** What one load fetched: the summaries, and the list it was asked for (null when none / not found). */
interface Loaded {
  listId: number | null;
  /** null when the summaries GET failed but the list itself loaded (the switcher keeps its last value). */
  lists: ShoppingListSummaryDto[] | null;
  list: ShoppingListDto | null;
  notFound: boolean;
}

const ITEM_CHANGED = 'That item changed — the list was refreshed.';
const ITEM_GONE = "That item couldn't be updated — the list was refreshed.";
const LIST_CHANGED = 'That list changed — refreshed.';

export class ShoppingListState {
  currentListId = $state<number | null>(null);
  lists = $state<ShoppingListSummaryDto[]>([]);
  list = $state<ShoppingListDto | null>(null);
  loading = $state(true);
  error = $state<string | null>(null);

  /** Item ids moved by a reorder that is still on the wire (the Resource keys only one write each). */
  #reordering = $state<ReadonlySet<number>>(new Set());
  /** Items of a failed reorder: pending until the next load lands (commit or error) and resyncs them. */
  #awaitingRefresh = $state<ReadonlySet<number>>(new Set());
  /** True once the current load reached an end (commit or error) — a load a write RETIRED did not. */
  #loadEnded = false;
  readonly #onListChange: (listId: number) => void;

  #resource = new Resource<Loaded>({
    fetch: async () => {
      const listId = this.currentListId;
      if (listId == null) return { listId, lists: await listLists(), list: null, notFound: false };
      // The summaries are secondary to the list on screen: a failed summaries GET keeps the previous switcher
      // (null = unchanged) rather than discard a list that loaded (council r7 — no availability regression
      // from loading them together).
      const listsP = listLists().catch(() => null);
      try {
        const [lists, list] = await Promise.all([listsP, getList(listId)]);
        return { listId, lists, list, notFound: false };
      } catch (e) {
        if (!(e instanceof ApiError && e.status === 404)) throw e;
        return { listId, lists: await listsP, list: null, notFound: true };
      }
    },
    commit: ({ listId, lists: fetchedLists, list, notFound }) => {
      this.#loadEnded = true;
      this.#awaitingRefresh = new Set(); // the refresh landed: a failed reorder's items are resynced
      const lists = fetchedLists ?? this.lists;
      this.lists = lists;
      if (listId == null) {
        // No list selected (first visit): default to the top one, then load it.
        this.list = null;
        const first = lists[0]?.id ?? null;
        if (first != null) this.#switchTo(first);
        return;
      }
      if (notFound) {
        // The list is gone (archived / deleted elsewhere): fall back to another one. A newer selection made
        // while this load was out superseded it (the Resource dropped it), so this can't hijack that choice.
        const fallback = lists.find((l) => l.id !== listId)?.id ?? null;
        if (fallback != null) {
          this.#switchTo(fallback);
          return;
        }
        this.list = null;
        this.error = 'Shopping list not found.';
        return;
      }
      this.list = list;
    },
    onLoadStart: () => {
      this.#loadEnded = false;
      this.loading = true;
      this.error = null;
    },
    onLoadError: (e) => {
      this.#loadEnded = true;
      this.#awaitingRefresh = new Set(); // a failed refresh releases them too (holding on would lock them for good)
      this.error = e instanceof Error ? e.message : String(e);
    },
    onLoadIdle: () => {
      // A load a write retired leaves the spinner up (the owed load follows) — on a first visit that keeps
      // "Loading…" instead of flashing "No active shopping lists".
      if (this.#loadEnded) this.loading = false;
    },
  });

  constructor(initialListId: number | null, onListChange: (listId: number) => void = () => {}) {
    this.currentListId = initialListId;
    this.#onListChange = onListChange;
  }

  /** Items with a write in flight — a second tap would carry the same token and 409 against the first. */
  get pendingIds(): ReadonlySet<number> {
    // Item ids only — the list-level 'favorite' key is not an item.
    const merged = new Set<number>();
    for (const k of this.#resource.pendingKeys) if (typeof k === 'number') merged.add(k);
    for (const id of this.#reordering) merged.add(id);
    for (const id of this.#awaitingRefresh) merged.add(id);
    return merged;
  }

  /** Load the current list + the summaries (initial load, list switch, liveness, Retry). */
  async load(): Promise<void> {
    await this.#resource.load();
  }

  /** Resolves once the refresh a write started has finished. */
  settled(): Promise<void> {
    return this.#resource.settled();
  }

  async selectList(id: number): Promise<void> {
    this.currentListId = id;
    this.#onListChange(id);
    await this.load();
  }

  /** Point at another list and load it (queued: called from inside a commit). */
  #switchTo(id: number): void {
    this.currentListId = id;
    this.#onListChange(id);
    queueMicrotask(() => void this.load());
  }

  /**
   * The list whose items are ON SCREEN — the one every item and list write targets (council r7). Item ids are
   * per list, so a write must never go to `currentListId` while the previous list's rows are still shown
   * (a list switch, create or archive has pointed at a new list whose load hasn't landed yet): deleting shown
   * item 3 would delete the OTHER list's item 3. Null when no list is shown.
   */
  #shownListId(): number | null {
    return this.list?.id ?? null;
  }

  /** The rows of `listId` if it is still the list on screen — a write's answer never lands on another list's rows. */
  #rowsOf(listId: number): ShoppingListItemDto[] | undefined {
    return this.list?.id === listId ? this.list.items : undefined;
  }

  #isPending(id: number): boolean {
    return this.#resource.isPending(id) || this.#reordering.has(id) || this.#awaitingRefresh.has(id);
  }

  // ── Item writes ─────────────────────────────────────────────────────────────

  /** Check / uncheck an item. Resolves to the write's outcome ('ok' → the caller may offer Undo). */
  async setChecked(item: ShoppingListItemDto, target: boolean): Promise<MutationOutcome> {
    const listId = this.#shownListId();
    if (!this.list || listId == null || this.#isPending(item.id)) return 'skipped';
    const prev = { isChecked: item.isChecked, checkedAt: item.checkedAt };
    const sentVersion = item.version;
    return this.#resource.mutate({
      key: item.id,
      optimistic: () => {
        item.isChecked = target;
        item.checkedAt = target ? new Date().toISOString() : null;
        return () => Object.assign(item, prev);
      },
      call: () => patchItem(listId, item.id, { isChecked: target, version: sentVersion }),
      commit: (updated) => adoptItem(this.#rowsOf(listId), item, updated, sentVersion),
      // 409 (the item changed since this tab read it) / 404 (it's gone) → undo, notice, refresh.
      messages: { conflict: ITEM_CHANGED, rejected: ITEM_GONE, failed: "Couldn't update that item right now." },
    });
  }

  async setQuantity(item: ShoppingListItemDto, delta: number): Promise<void> {
    const listId = this.#shownListId();
    if (!this.list || listId == null || item.quantity == null || this.#isPending(item.id)) return;
    const next = item.quantity + delta;
    if (next < 1) return;
    const prev = item.quantity;
    const sentVersion = item.version;
    await this.#resource.mutate({
      key: item.id,
      optimistic: () => {
        item.quantity = next;
        return () => {
          item.quantity = prev;
        };
      },
      call: () => patchItem(listId, item.id, { quantity: next, version: sentVersion }),
      commit: (updated) => adoptItem(this.#rowsOf(listId), item, updated, sentVersion),
      messages: { conflict: ITEM_CHANGED, rejected: ITEM_GONE, failed: "Couldn't update that item right now." },
    });
  }

  /** Add an item (not optimistic: the server assigns the id). */
  async addItem(value: Parameters<typeof addItem>[1]): Promise<MutationOutcome> {
    const listId = this.#shownListId();
    if (listId == null || !this.list) return 'skipped';
    return this.#resource.mutate({
      call: () => addItem(listId, value),
      commit: (created) => {
        if (this.list?.id === listId) this.list.items = [...this.list.items, created];
      },
      messages: {
        conflict: LIST_CHANGED,
        rejected: "That item couldn't be added — the list was refreshed.",
        failed: "Couldn't add that item right now.",
      },
    });
  }

  /**
   * Edit an item from the dialog. It carries the token the dialog opened with: a 409 means someone else changed the
   * item since — the list refreshes and the user redoes the edit on their version.
   */
  async editItem(
    initial: ShoppingListItemDto,
    value: { name: string; quantity: number | null; unit: string | null; category: string },
  ): Promise<MutationOutcome> {
    const listId = this.#shownListId();
    if (listId == null || !this.list || this.#isPending(initial.id)) return 'skipped'; // incl. a reorder in flight
    return this.#resource.mutate({
      key: initial.id,
      call: () =>
        patchItem(listId, initial.id, {
          version: initial.version,
          name: value.name,
          quantity: value.quantity,
          unit: value.unit,
          category: value.category,
        }),
      commit: (updated) => {
        const items = this.#rowsOf(listId);
        const idx = items?.findIndex((i) => i.id === updated.id) ?? -1;
        if (items && idx >= 0) items[idx] = updated;
      },
      messages: {
        conflict: ITEM_CHANGED,
        rejected: "That item couldn't be saved — the list was refreshed.",
        failed: "Couldn't save that item right now.",
      },
    });
  }

  async deleteItem(item: ShoppingListItemDto): Promise<void> {
    const listId = this.#shownListId();
    if (listId == null || !this.list || this.#isPending(item.id)) return;
    await this.#resource.mutate({
      key: item.id,
      call: () => deleteItem(listId, item.id),
      commit: () => {
        if (this.list?.id === listId) this.list.items = this.list.items.filter((i) => i.id !== item.id);
      },
      messages: { conflict: LIST_CHANGED, rejected: ITEM_GONE, failed: "Couldn't delete that item right now." },
    });
  }

  /**
   * Commit a drag-reorder of one category zone (the drag itself already re-sorted `list.items`). The moved items
   * stay pending until the answer brings their new tokens: a tap now would carry the pre-reorder token and 409
   * once the reorder commits (quest 8cf3c806). Any failure refreshes the list (the order on screen is unsaved).
   */
  async reorder(category: string, reordered: ShoppingListItemDto[]): Promise<void> {
    const listId = this.#shownListId();
    if (listId == null) return;
    const updates: SortOrderUpdate[] = reordered.map((item, index) => ({ itemId: item.id, sortOrder: index, category }));
    const sent = new Map(
      reordered.map((item, index) => [item.id, { heldVersion: item.version, sortOrder: index, category }]),
    );
    const movedIds = reordered.map((item) => item.id);
    // A drop that moves an item another write still holds (an earlier reorder still on the wire, a tap) is
    // not sent: the two writes would release each other's guards and leave stale tokens (council r7
    // challenge). Put the server's order back instead — the load is owed until those writes settle.
    if (movedIds.some((id) => this.#isPending(id))) {
      showToast({ message: 'Still saving the last change — the list was refreshed.', kind: 'info' });
      void this.load();
      return;
    }
    this.#reordering = new Set([...this.#reordering, ...movedIds]);
    let released = false;
    try {
      const outcome = await this.#resource.mutate({
        call: () => updateSortOrders(listId, updates),
        // Adopt the answered tokens only where the reorder read the row at the token this tab held.
        commit: (result) => adoptReorderVersions(this.#rowsOf(listId), sent, result.items),
        reload: 'on-error',
        messages: { conflict: LIST_CHANGED, rejected: LIST_CHANGED, failed: "Couldn't save the new order — refreshed." },
      });
      released = outcome === 'ok';
    } finally {
      // A failed reorder (or a throwing adopt) leaves rows whose order/tokens are unsaved: they stay pending
      // until a load lands — the refresh it asked for, which runs when the LAST pending write settles, maybe
      // not this one (council r7 + challenge NC1). A success releases them now (the answer brought the tokens).
      if (!released) this.#awaitingRefresh = new Set([...this.#awaitingRefresh, ...movedIds]);
      const next = new Set(this.#reordering);
      for (const id of movedIds) next.delete(id);
      this.#reordering = next;
    }
  }

  // ── List writes ─────────────────────────────────────────────────────────────

  async toggleFavorite(): Promise<void> {
    const listId = this.#shownListId();
    const list = this.list;
    if (listId == null || !list) return;
    const prev = list.isFavorite;
    await this.#resource.mutate({
      key: 'favorite',
      optimistic: () => {
        list.isFavorite = !prev;
        return () => {
          list.isFavorite = prev;
        };
      },
      call: () => toggleFavorite(listId),
      commit: (r) => {
        list.isFavorite = r.isFavorite;
      },
      reload: 'always', // the switcher's favorites-first order changes
      messages: { conflict: LIST_CHANGED, rejected: LIST_CHANGED, failed: "Couldn't update the favorite right now." },
    });
  }

  /** A list-level write that reloads afterwards; rejects with the write's own error (the caller surfaces it). */
  async #listWrite<R>(call: () => Promise<R>, commit?: (result: R) => void): Promise<R> {
    return this.#resource.run({ call, commit });
  }

  async regenerate(): Promise<boolean> {
    const listId = this.#shownListId();
    if (listId == null) return false;
    return this.#surface(async () => {
      await this.#listWrite(
        () => regenerateList(listId),
        (updated) => {
          if (this.list?.id === listId) this.list = updated;
        },
      );
      showToast({ message: 'List regenerated from the meal plan', kind: 'success' });
    });
  }

  async rename(newName: string): Promise<boolean> {
    const listId = this.#shownListId();
    if (listId == null || !this.list) return false;
    return this.#surface(() =>
      this.#listWrite(
        () => renameList(listId, newName),
        (r) => {
          if (this.list?.id === listId) this.list.name = r.name;
        },
      ),
    );
  }

  /** Create a list and switch to it (the reload at settle loads the new list). */
  async createList(name: string): Promise<boolean> {
    return this.#surface(() =>
      this.#listWrite(
        () => createList(name),
        (created) => this.#point(created.id),
      ),
    );
  }

  async generate(body: { startDate: string; endDate: string }): Promise<boolean> {
    return this.#surface(async () => {
      const created = await this.#listWrite(
        () => generateFromMealPlan(body),
        (c) => this.#point(c.id),
      );
      showToast({ message: `Generated with ${created.itemCount} items`, kind: 'success', durationMs: 3000 });
    });
  }

  async clearChecked(): Promise<void> {
    const listId = this.#shownListId();
    if (listId == null) return;
    await this.#surface(async () => {
      const { removed } = await this.#listWrite(() => clearChecked(listId));
      showToast({ message: `Cleared ${removed} checked items`, kind: 'success' });
    });
  }

  /** Archive the current list and move to the next one (the reload at settle loads it). */
  async archive(): Promise<void> {
    const listId = this.#shownListId();
    if (listId == null) return;
    await this.#surface(async () => {
      await this.#listWrite(
        () => archiveList(listId),
        () => {
          const next = this.lists.find((l) => l.id !== listId)?.id ?? null;
          if (next != null) this.#point(next);
          else {
            this.currentListId = null;
            this.list = null;
          }
        },
      );
      showToast({ message: 'List archived', kind: 'info' });
    });
  }

  /** Point at a list without loading (inside a write window: the settle load fetches it). */
  #point(id: number): void {
    this.currentListId = id;
    this.#onListChange(id);
  }

  /** Run a list action; a failure lands in the inline error banner (as before). Returns success. */
  async #surface(action: () => Promise<unknown>): Promise<boolean> {
    try {
      await action();
      return true;
    } catch (e) {
      this.error = e instanceof Error ? e.message : String(e);
      return false;
    }
  }
}
