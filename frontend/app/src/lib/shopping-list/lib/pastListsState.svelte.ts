// ─────────────────────────────────────────────────────────────────────────
// Past shopping lists (quest f63bb90a) — the state behind PastApp.svelte, moved
// out of the component so its ordering can be unit-tested (quest 41337b6f,
// spec §5 row 8). One instance per PastApp mount (not a singleton).
//
// Two Resources own WHEN a response may land:
//   • lists  — the archived list + the active lists (pick targets);
//   • detail — the expanded archived list's read-only detail.
// A delete runs inside BOTH write windows, so neither an archived-list GET nor a
// detail GET that overlaps it can commit the deleted list afterwards (the old
// `detailSeq` was never bumped by a delete, so a detail answered after the
// delete re-expanded a list that no longer exists).
//
// ⚠ Svelte 5 rune rule: the runes are fields on the instance; the component
// holds the instance, never a reassigned module-level $state.
// ─────────────────────────────────────────────────────────────────────────

import type { ArchivedListSummaryDto, ShoppingListDto, ShoppingListSummaryDto } from './types';
import { addItem, deleteList, getArchivedList, listArchived, listLists, restoreList } from './api';
import { Resource } from '$lib/data/resource.svelte';
import { showToast } from '$lib/shared/toast-store.svelte';

const message = (e: unknown): string => (e instanceof Error ? e.message : String(e));

export class PastListsState {
  archived = $state<ArchivedListSummaryDto[]>([]);
  activeLists = $state<ShoppingListSummaryDto[]>([]);
  favoritesOnly = $state(false);
  loading = $state(true);
  error = $state<string | null>(null);

  /** The expanded list's read-only detail, or null when collapsed. */
  detail = $state<ShoppingListDto | null>(null);
  detailLoading = $state(false);
  pickedIds = $state(new Set<number>());
  targetListId = $state<number | null>(null);
  adding = $state(false);

  /** The list the detail Resource fetches: set on expand, cleared on collapse or on its delete. */
  #detailTarget: number | null = null;
  /** The lists have loaded once — later loads refresh in place. */
  #listsLoaded = false;

  #lists = new Resource<[ArchivedListSummaryDto[], ShoppingListSummaryDto[]]>({
    fetch: () => Promise.all([listArchived(this.favoritesOnly), listLists()]),
    commit: ([past, active]) => {
      this.archived = past;
      this.activeLists = active;
      this.#listsLoaded = true;
      // Favorites-first is the server's sort for actives too — default the pick target to the top.
      if (this.targetListId == null || !active.some((l) => l.id === this.targetListId)) {
        this.targetListId = active[0]?.id ?? null;
      }
    },
    onLoadStart: () => {
      // "Loading…" replaces the rows only until the first load lands; later loads (a filter flip, the
      // reload after a write) refresh in place, so a stalled reload after a failed delete never takes
      // the rows away (slim-review on fca#153).
      if (!this.#listsLoaded) this.loading = true;
      this.error = null;
    },
    onLoadError: (e) => {
      this.error = message(e);
    },
    onLoadIdle: () => {
      this.loading = false;
    },
  });

  #detail = new Resource<ShoppingListDto | null>({
    fetch: () => {
      const target = this.#detailTarget;
      return target == null ? Promise.resolve(null) : getArchivedList(target);
    },
    commit: (loaded) => {
      this.detail = loaded;
      this.pickedIds = new Set();
    },
    onLoadStart: () => {
      this.detailLoading = this.#detailTarget != null;
    },
    onLoadError: (e) => {
      this.error = message(e);
    },
    onLoadIdle: () => {
      this.detailLoading = false;
    },
  });

  async load(): Promise<void> {
    await this.#lists.load();
  }

  /** Expand a list's detail, or collapse it when it is the one shown. A newer expand/collapse supersedes. */
  async toggleDetail(listId: number): Promise<void> {
    if (this.detail?.id === listId) {
      this.#detailTarget = null;
      this.detail = null;
      this.pickedIds = new Set();
    } else {
      this.#detailTarget = listId;
    }
    await this.#detail.load(); // a collapse loads nothing — it only supersedes an expand in flight
  }

  togglePick(itemId: number): void {
    const next = new Set(this.pickedIds);
    if (next.has(itemId)) next.delete(itemId);
    else next.add(itemId);
    this.pickedIds = next;
  }

  /** Reopen an archived list. Returns true on success (the component navigates to it). */
  async reopen(listId: number): Promise<boolean> {
    try {
      await this.#lists.run({ call: () => restoreList(listId), reload: 'never' });
    } catch (e) {
      this.error = message(e);
      return false;
    }
    showToast({ message: 'List reopened', kind: 'success' });
    return true;
  }

  /** Permanently delete an archived list — inside both windows (lists + detail). */
  async remove(id: number): Promise<void> {
    try {
      await this.#detail.run({
        // reload 'never': a detail load it retired is still owed, and fetches nothing once the
        // deleted list is no longer the target.
        reload: 'never',
        call: () =>
          this.#lists.run({
            call: () => deleteList(id),
            commit: () => {
              if (this.#detailTarget === id) this.#detailTarget = null;
              if (this.detail?.id === id) this.detail = null;
              this.archived = this.archived.filter((l) => l.id !== id);
            },
          }),
      });
    } catch (e) {
      this.error = message(e);
      return;
    }
    showToast({ message: 'List permanently deleted', kind: 'info' });
  }

  /**
   * Copy picked rows into the target ACTIVE list via AddItem — they arrive manual + unchecked.
   * Each success is removed from the selection AS IT LANDS, so a retry after a partial failure
   * only re-sends the items that never made it (council round 1: retry-safety).
   */
  async addPicked(): Promise<void> {
    const detail = this.detail;
    const targetListId = this.targetListId;
    if (!detail || targetListId == null || this.pickedIds.size === 0) return;
    this.adding = true;
    const picked = detail.items.filter((i) => this.pickedIds.has(i.id));
    let added = 0;
    try {
      await this.#lists.run({
        call: async () => {
          for (const item of picked) {
            await addItem(targetListId, {
              name: item.name,
              quantity: item.quantity,
              unit: item.unit,
              category: item.category,
            });
            added += 1;
            const next = new Set(this.pickedIds);
            next.delete(item.id);
            this.pickedIds = next;
          }
        },
      });
    } catch (e) {
      this.error = message(e);
      if (added > 0) {
        showToast({
          message: `Added ${added} of ${picked.length} — the rest stay selected; retry adds only those.`,
          kind: 'info',
        });
      }
      return;
    } finally {
      this.adding = false;
    }
    // Outside the write's error handler (council r4): a throwing toast is not an add failure.
    const target = this.activeLists.find((l) => l.id === targetListId);
    showToast({
      message: `Added ${added} item${added === 1 ? '' : 's'} to ${target?.name ?? 'the list'}`,
      kind: 'success',
    });
  }
}
