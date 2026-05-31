// ─────────────────────────────────────────────────────────────────────────
// Board store — the SINGLE source of truth for the chores island.
//
// One `ChoreBoardDto` is fetched (App.svelte) and held here. Every lens is a
// CLIENT-SIDE grouping of that one payload (M11) — switching lenses never
// refetches. The derived groupings below (Needs-attention sections, Rooms,
// Up-for-grabs, Mine) all read the same `board` and recompute reactively.
//
// ⚠ Svelte 5 rune rule (global CORRECTION): never `export` a reassigned
// `$state`/`$derived` from a module. We wrap the mutable state in a class
// instance and export the instance — the runes live as `$state`/`$derived`
// fields on the object, which is the supported pattern.
//
// ⚠ M5/M6: this store NEVER recomputes dueness/decay. `colorTier`/`dueState`
// are taken straight from the server DTO. No `new Date('YYYY-MM-DD')` anywhere.
// ─────────────────────────────────────────────────────────────────────────

import type {
  ChoreBoardDto,
  ChoreDto,
  RoomRollupDto,
  MemberDto,
  ChoreLensId,
  DueState,
} from './types';

/** One sectioned bucket of the Needs-attention lens. */
export interface NeedsAttentionSection {
  id: 'falling-behind' | 'due-now' | 'coming-up';
  title: string;
  chores: ChoreDto[];
}

/** A room with its rollup metadata + the chores that belong to it. */
export interface RoomGroup {
  rollup: RoomRollupDto;
  chores: ChoreDto[];
}

/**
 * The in-board filter applied within the Needs-attention lens
 * (Everything / Up-for-grabs / Mine chips — S2 of the UX recommendation).
 */
export type AttentionFilter = 'everything' | 'up-for-grabs' | 'mine';

/**
 * Rank a chore for "dirtiest-first" ordering WITHIN a section. We order by the
 * server-computed `dueState` (overdue first), then by `nextDueAt` soonest, then
 * by id for stability. We do NOT recompute dueness — we only sort on the
 * server's verdict (M5).
 */
const DUE_STATE_RANK: Record<DueState, number> = {
  overdue: 0,
  dueToday: 1,
  scheduled: 2,
  notDue: 3,
};

function sortDirtiestFirst(a: ChoreDto, b: ChoreDto): number {
  const r = DUE_STATE_RANK[a.dueState] - DUE_STATE_RANK[b.dueState];
  if (r !== 0) return r;
  // Soonest next-due first; nulls sort last. Compare ISO strings lexically —
  // ISO-8601 UTC strings are lexically ordered, so this is safe WITHOUT parsing
  // them into Date objects (M5 — never construct dates for logic).
  const an = a.nextDueAt;
  const bn = b.nextDueAt;
  if (an !== bn) {
    if (an == null) return 1;
    if (bn == null) return -1;
    return an < bn ? -1 : 1;
  }
  return a.id - b.id;
}

class BoardStore {
  /** The one fetched payload. null until the first load resolves. */
  board = $state<ChoreBoardDto | null>(null);
  loading = $state(true);
  /** Human-readable error message; null when healthy. */
  error = $state<string | null>(null);

  /** The active lens (ViewSwitcher). Defaults to Needs-attention. */
  lens = $state<ChoreLensId>('needs-attention');
  /** The in-Needs-attention filter chip selection. */
  attentionFilter = $state<AttentionFilter>('everything');

  /** This household member's userId — set once on init from the shell context. */
  currentUserId = $state(0);

  // ── Lookups off the single payload ─────────────────────────────────────

  /** userId → member, for owner/assignee avatar rendering. */
  membersById = $derived.by(() => {
    const map = new Map<number, MemberDto>();
    for (const m of this.board?.members ?? []) map.set(m.userId, m);
    return map;
  });

  /** All board chores (empty when unloaded). */
  chores = $derived<ChoreDto[]>(this.board?.chores ?? []);

  /** id → chore, used to resolve `needsAttentionChoreIds` into chores. */
  choresById = $derived.by(() => {
    const map = new Map<number, ChoreDto>();
    for (const c of this.chores) map.set(c.id, c);
    return map;
  });

  // ── Needs-attention lens (WP-10 default) ────────────────────────────────
  //
  // Inclusion + base order come from the SERVER's `needsAttentionChoreIds`
  // (overdue → dueToday → pile, dirtiest-first — computed server-side, M11/M5).
  // We then split into the three display sections by the server `dueState` /
  // assignment, and apply the active filter chip.

  /** The needs-attention chores, in server order, after the active filter chip. */
  needsAttentionChores = $derived.by(() => {
    const board = this.board;
    if (!board) return [] as ChoreDto[];
    const byId = this.choresById;
    const ordered: ChoreDto[] = [];
    for (const id of board.needsAttentionChoreIds) {
      const c = byId.get(id);
      if (c) ordered.push(c);
    }
    return ordered.filter((c) => this.passesAttentionFilter(c));
  });

  /** Needs-attention split into Falling behind → Due now → Coming up. */
  needsAttentionSections = $derived.by<NeedsAttentionSection[]>(() => {
    const chores = this.needsAttentionChores;
    const fallingBehind: ChoreDto[] = [];
    const dueNow: ChoreDto[] = [];
    const comingUp: ChoreDto[] = [];
    for (const c of chores) {
      if (c.dueState === 'overdue') fallingBehind.push(c);
      else if (c.dueState === 'dueToday') dueNow.push(c);
      else comingUp.push(c); // scheduled / notDue pile chores
    }
    // Server order is already dirtiest-first; keep it (do NOT re-sort and risk
    // diverging from the server verdict).
    const sections: NeedsAttentionSection[] = [
      { id: 'falling-behind', title: 'Falling behind', chores: fallingBehind },
      { id: 'due-now', title: 'Due now', chores: dueNow },
      { id: 'coming-up', title: 'Coming up', chores: comingUp },
    ];
    return sections.filter((s) => s.chores.length > 0);
  });

  // ── Rooms lens (grouping seam — full UI is WP-12) ────────────────────────
  //
  // Buckets the one payload by roomId against the server rollups, including the
  // virtual General group (roomId === null). WP-12 builds the rooms-drill UI;
  // this derived grouping is the seam it will consume.

  roomGroups = $derived.by<RoomGroup[]>(() => {
    const board = this.board;
    if (!board) return [];
    const byRoom = new Map<number | null, ChoreDto[]>();
    for (const c of this.chores) {
      const key = c.roomId ?? null;
      if (!byRoom.has(key)) byRoom.set(key, []);
      byRoom.get(key)!.push(c);
    }
    // Server provides the rollups (incl. General) pre-sorted by sortOrder.
    return [...board.rooms]
      .sort((a, b) => a.sortOrder - b.sortOrder)
      .map((rollup) => ({
        rollup,
        chores: (byRoom.get(rollup.roomId ?? null) ?? []).slice().sort(sortDirtiestFirst),
      }));
  });

  // ── Up-for-grabs lens (grouping seam — full UI is WP-12) ─────────────────
  //
  // Unclaimed pile chores PLUS stale claims (isClaimStale — pile-eligible per
  // WP-04/05 stale-claim UX). Dirtiest-first. WP-12 builds the lane UI.

  upForGrabsChores = $derived.by<ChoreDto[]>(() =>
    this.chores
      .filter((c) => c.assignmentKind === 'none' || c.isClaimStale)
      .slice()
      .sort(sortDirtiestFirst),
  );

  // ── Mine lens (grouping seam — full UI is WP-12) ─────────────────────────
  //
  // Chores owned by OR actively claimed/assigned to the current user (excluding
  // stale claims, which are effectively back in the pile). Dirtiest-first.

  mineChores = $derived.by<ChoreDto[]>(() => {
    const uid = this.currentUserId;
    return this.chores
      .filter((c) => {
        const heldByMe =
          c.assigneeUserId === uid && c.assignmentKind !== 'none' && !c.isClaimStale;
        const ownedByMe = c.ownerUserId === uid;
        return heldByMe || ownedByMe;
      })
      .slice()
      .sort(sortDirtiestFirst);
  });

  // ── Helpers ──────────────────────────────────────────────────────────────

  private passesAttentionFilter(c: ChoreDto): boolean {
    switch (this.attentionFilter) {
      case 'up-for-grabs':
        return c.assignmentKind === 'none' || c.isClaimStale;
      case 'mine':
        return (
          (c.assigneeUserId === this.currentUserId &&
            c.assignmentKind !== 'none' &&
            !c.isClaimStale) ||
          c.ownerUserId === this.currentUserId
        );
      case 'everything':
      default:
        return true;
    }
  }

  setBoard(next: ChoreBoardDto): void {
    this.board = next;
    this.error = null;
  }

  setLens(next: ChoreLensId): void {
    this.lens = next;
  }

  setAttentionFilter(next: AttentionFilter): void {
    this.attentionFilter = next;
  }
}

/**
 * The single shared store instance. Components import this and read its
 * `$state`/`$derived` fields reactively. We export the INSTANCE (not the runes
 * themselves) so the CORRECTIONS rune-export rule is respected.
 */
export const boardStore = new BoardStore();

/** Resolve a member for avatar display; null when unknown/unassigned. */
export function memberFor(userId: number | null | undefined): MemberDto | null {
  if (userId == null) return null;
  return boardStore.membersById.get(userId) ?? null;
}
