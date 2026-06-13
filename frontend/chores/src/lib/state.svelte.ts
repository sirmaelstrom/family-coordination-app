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
  ChoreEquityDto,
  EquityWindow,
  RoomRollupDto,
  MemberDto,
  ChoreLensId,
  DueState,
  RosterState,
} from './types';
import { CHORE_LENSES } from './types';
import {
  ApiError,
  claimChore,
  takeChore,
  dropChore,
  completeChore,
  handOffChore,
  assignRoster,
  commitRoster,
  leaveRoster,
  createChore,
  deleteChore,
  updateChore,
  seedStarter as apiSeedStarter,
  setDefaultView,
  getEquity,
  type CreateChoreRequest,
  type UpdateChoreRequest,
  type CompleteRequest,
} from './api';
import { showToast } from './toasts.svelte';

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
 * Per-member current-load summary for the Mine lens's household load strip
 * (the v1.1 equity seed — display-only over the already-loaded board, no
 * schema change, no points engine). We surface the count a member is actively
 * holding (claimed or assigned, excluding stale claims) — NOT a points total.
 *
 * ⚠ M5/M6: we deliberately do NOT compute a "completed today" count here — the
 * board DTO's `lastCompletedAt` carries no actor and "today" would require
 * client-side timezone day-boundary math, which is forbidden. The held count is
 * a pure assignment-state grouping of the payload (no date logic).
 */
export interface MemberLoad {
  member: MemberDto;
  /** Chores this member is actively holding (claimed/assigned, non-stale). */
  heldCount: number;
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

  /**
   * The persisted roaming default lens (per-user, server-stored on
   * `User.ChoresDefaultView`). Mirrors `board.userDefaultView` (null ⇒
   * Needs-attention). Kept as a local field so the "set as default" affordance
   * reflects success immediately without a board refetch.
   */
  defaultView = $state<ChoreLensId | null>(null);
  /** True while a `setDefaultView` PATCH is in flight (disables the control). */
  savingDefaultView = $state(false);
  /** Guards `initLensFromDefault` so the landing lens is set only on first load. */
  private defaultViewInitialized = false;

  /**
   * Chore ids with an in-flight mutation. Drives per-card disabled state +
   * double-submit prevention (M-in-flight). A `Set` swapped by reference so the
   * `$state` reactivity fires.
   */
  pendingChoreIds = $state(new Set<number>());

  // ── Equity lens (the one non-board fetcher — M11) ─────────────────────────
  //
  // The Equity lens reads a SEPARATE cached payload (GET /api/chores/equity),
  // not the board. It is the only lens that fetches; the four v1.0 lenses group
  // the one board payload. The cached payload goes STALE when completions/chores
  // change, so any such action invalidates it (`equityLoaded = false`) and — if
  // the equity lens is currently open — reloads it immediately. App.svelte's
  // $effect performs the fetch-on-open (and on window change). All values are
  // server-computed; NO client date math (MN9).

  /** The cached equity payload. null until first loaded (or after invalidation+reload). */
  equity = $state<ChoreEquityDto | null>(null);
  /** The reporting window. Switching it triggers a reload via the App effect. */
  equityWindow = $state<EquityWindow>('week');
  /** True while a `loadEquity()` fetch is in flight. */
  equityLoading = $state(false);
  /** Human-readable equity error; null when healthy. */
  equityError = $state<string | null>(null);
  /** True once a fetch for the CURRENT window has resolved successfully. The
   *  App effect loads when `lens === 'equity' && !equityLoaded`; invalidation
   *  flips this back to false so the next view (or the active lens) reloads. */
  equityLoaded = $state(false);

  /**
   * The board refetch hook, wired by App.svelte (`store.setRefresh(loadBoard)`).
   * The mutation layer calls it to reconcile after a 409 (xmin conflict — M7/M12)
   * or any other 4xx rejection, so the user sees the true server state. Liveness
   * shares the SAME loader, so a post-mutation refetch reuses it.
   */
  private refresh: (() => Promise<void>) | null = null;

  // ── Lookups off the single payload ─────────────────────────────────────

  /** userId → member, for owner/assignee avatar rendering. */
  membersById = $derived.by(() => {
    const map = new Map<number, MemberDto>();
    for (const m of this.board?.members ?? []) map.set(m.userId, m);
    return map;
  });

  /**
   * roomId → room rollup, for the per-card room locator chip. REAL rooms only —
   * the virtual General group (roomId === null, roomless chores) is excluded so a
   * roomless card shows no chip rather than a noisy "General" tag.
   */
  roomsById = $derived.by(() => {
    const map = new Map<number, RoomRollupDto>();
    for (const r of this.board?.rooms ?? []) {
      if (r.roomId != null) map.set(r.roomId, r);
    }
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

  upForGrabsChores = $derived.by<ChoreDto[]>(() => {
    const uid = this.currentUserId;
    return this.chores
      .filter((c) => {
        // Multi-person chores are up-for-grabs to anyone NOT yet on the roster —
        // they can join ("I'm in") or just do their part. Members already on the
        // roster (assigned / in / done) see it in Mine instead.
        if (c.requiredCount > 1 && c.completedCount < c.requiredCount) {
          return !c.roster.some((m) => m.userId === uid);
        }
        return c.assignmentKind === 'none' || c.isClaimStale;
      })
      .slice()
      .sort(sortDirtiestFirst);
  });

  // ── Mine lens (grouping seam — full UI is WP-12) ─────────────────────────
  //
  // Chores owned by OR actively claimed/assigned to the current user (excluding
  // stale claims, which are effectively back in the pile). Dirtiest-first.

  mineChores = $derived.by<ChoreDto[]>(() => {
    const uid = this.currentUserId;
    return this.chores
      .filter((c) => {
        // A multi-person chore is "mine" if I'm on its roster in any state
        // (assigned/in/done) — awaiting my confirm, my part, or done-and-waiting
        // on the others. If I'm not on the roster it's an up-for-grabs chore, not
        // mine. Multi-person chores aren't claimable, so the claim/owner rules
        // below don't apply to them.
        if (c.requiredCount > 1 && c.completedCount < c.requiredCount) {
          return c.roster.some((m) => m.userId === uid);
        }
        const heldByMe =
          c.assigneeUserId === uid && c.assignmentKind !== 'none' && !c.isClaimStale;
        const ownedByMe = c.ownerUserId === uid;
        return heldByMe || ownedByMe;
      })
      .slice()
      .sort(sortDirtiestFirst);
  });

  // ── Household load strip (Mine lens — the v1.1 equity SEED) ───────────────
  //
  // Per-member count of actively-held chores (claimed/assigned, non-stale),
  // grouped off the one payload (M11). Display-only — NOT a points/equity
  // engine, NOT a "completed today" tally (no actor in the DTO + would need
  // client tz day math — M5). Ordered current-user first, then by load desc.

  memberLoads = $derived.by<MemberLoad[]>(() => {
    const board = this.board;
    if (!board) return [];
    const held = new Map<number, number>();
    for (const c of this.chores) {
      if (c.assigneeUserId != null && c.assignmentKind !== 'none' && !c.isClaimStale) {
        held.set(c.assigneeUserId, (held.get(c.assigneeUserId) ?? 0) + 1);
      }
    }
    const uid = this.currentUserId;
    return board.members
      .map((member) => ({ member, heldCount: held.get(member.userId) ?? 0 }))
      .sort((a, b) => {
        // Current user first, then heavier load first, then name for stability.
        if (a.member.userId === uid && b.member.userId !== uid) return -1;
        if (b.member.userId === uid && a.member.userId !== uid) return 1;
        if (a.heldCount !== b.heldCount) return b.heldCount - a.heldCount;
        return a.member.displayName.localeCompare(b.member.displayName);
      });
  });

  // ── Helpers ──────────────────────────────────────────────────────────────

  private passesAttentionFilter(c: ChoreDto): boolean {
    // Co-sign chores partition by whether the viewer has signed (mirrors
    // upForGrabsChores / mineChores): not-yet-signed → up-for-grabs, signed →
    // mine. They are never claimable, so the claim/owner rules don't apply.
    const coSignInProgress = c.requiredCount > 1 && c.completedCount < c.requiredCount;
    const uid = this.currentUserId;
    const onRoster = c.roster.some((m) => m.userId === uid);
    switch (this.attentionFilter) {
      case 'up-for-grabs':
        return coSignInProgress
          ? !onRoster
          : c.assignmentKind === 'none' || c.isClaimStale;
      case 'mine':
        return coSignInProgress
          ? onRoster
          : (c.assigneeUserId === uid && c.assignmentKind !== 'none' && !c.isClaimStale) ||
              c.ownerUserId === uid;
      case 'everything':
      default:
        return true;
    }
  }

  setBoard(next: ChoreBoardDto): void {
    this.board = next;
    this.error = null;
    // Keep the local default mirror in sync with the authoritative payload, and
    // (on the FIRST load only) land on the user's roaming default lens.
    this.defaultView = coerceLens(next.userDefaultView);
    this.initLensFromDefault();
    // The board refetch path (loadBoard / setRefresh / liveness) may have
    // included a completion from another user, so the cached equity payload is
    // potentially stale. Invalidate (and reload if the equity lens is active).
    this.invalidateEquity();
  }

  setLens(next: ChoreLensId): void {
    this.lens = next;
  }

  setAttentionFilter(next: AttentionFilter): void {
    this.attentionFilter = next;
  }

  // ── Equity lens fetch + invalidation ──────────────────────────────────────

  /** Switch the reporting window. The window change drops the cache so the App
   *  effect reloads for the new window (it watches `equityWindow`). */
  setEquityWindow(next: EquityWindow): void {
    if (this.equityWindow === next) return;
    this.equityWindow = next;
    this.equityLoaded = false;
  }

  /**
   * Fetch the equity payload for the CURRENT window. Called by App.svelte's
   * $effect when the equity lens is open and the cache is stale (fetch-once per
   * window). Sets `equityLoaded` on success so the effect doesn't refetch on
   * every reactive tick.
   */
  async loadEquity(): Promise<void> {
    if (this.equityLoading) return;
    this.equityLoading = true;
    this.equityError = null;
    const window = this.equityWindow;
    try {
      const result = await getEquity(window);
      // Guard against a window switch landing mid-flight — only commit if the
      // requested window is still the active one.
      if (this.equityWindow === window) {
        this.equity = result;
        this.equityLoaded = true;
      }
    } catch (e) {
      this.equityError =
        e instanceof ApiError
          ? `Couldn't load the equity view (HTTP ${e.status}).`
          : "Couldn't load the equity view right now.";
    } finally {
      this.equityLoading = false;
    }
  }

  /**
   * Invalidate the cached equity payload — it's a separate cached read that goes
   * stale whenever completions/chores change (council MAJOR). Drops the cache and,
   * if the equity lens is currently open, reloads it immediately so the user
   * never sees a stale distribution. Called after `complete(...)` succeeds and
   * on every board refetch (`setBoard`). WP-10's `seedStarter()` calls this too.
   */
  invalidateEquity(): void {
    this.equityLoaded = false;
    if (this.lens === 'equity') {
      void this.loadEquity();
    }
  }

  // ── Roaming per-user default view (S10/D18) ───────────────────────────────
  //
  // The default lens is PER-USER and SERVER-PERSISTED (User.ChoresDefaultView),
  // so it roams across devices — NOT localStorage (D18). It arrives on the board
  // payload as `userDefaultView` (null ⇒ Needs-attention); no separate GET. The
  // PATCH allowlist validates against the SAME canonical lens ids (ChoreLens.All).

  /**
   * On the FIRST board load, open the lens the user pinned as their default
   * (null ⇒ Needs-attention). Runs once — later refetches (liveness, post-
   * mutation reconcile) must NOT yank the user off whatever lens they switched
   * to in-session.
   */
  private initLensFromDefault(): void {
    if (this.defaultViewInitialized) return;
    this.defaultViewInitialized = true;
    this.lens = this.defaultView ?? 'needs-attention';
  }

  /** Is the given lens the user's current persisted default? */
  isDefaultView(lens: ChoreLensId): boolean {
    return (this.defaultView ?? 'needs-attention') === lens;
  }

  /**
   * Pin a lens as the user's roaming default (persists via PATCH
   * /api/chores/me/default-view). The lens switch itself is local + instant
   * (M11 — no refetch); this only writes the preference. On an ApiError we keep
   * the local lens but surface a NON-BLOCKING toast — the switch still works,
   * only the persistence failed.
   */
  async saveDefaultView(lens: ChoreLensId): Promise<void> {
    if (this.savingDefaultView) return;
    // Already the default — no-op (the control renders as "current default").
    if (this.isDefaultView(lens)) return;
    this.savingDefaultView = true;
    const previous = this.defaultView;
    // Optimistic: reflect the new default at once.
    this.defaultView = lens;
    if (this.board) this.board.userDefaultView = lens;
    try {
      const result = await setDefaultView(lens);
      // Server echoes the persisted value; reconcile to it.
      const confirmed = coerceLens(result.view);
      this.defaultView = confirmed;
      if (this.board) this.board.userDefaultView = result.view;
      showToast({ message: 'This is now your default view.', kind: 'success' });
    } catch (e) {
      // Roll back the optimistic default; the lens stays switched (local only).
      this.defaultView = previous;
      if (this.board) this.board.userDefaultView = previous;
      const msg =
        e instanceof ApiError
          ? "Couldn't save your default view — it'll stay this session only."
          : "Couldn't save your default view right now.";
      showToast({ message: msg, kind: 'info' });
    } finally {
      this.savingDefaultView = false;
    }
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Mutation layer (WP-11) — optimistic updates + xmin-409 reconciliation.
  //
  // MODEL (every action follows this shape via `runOptimistic`):
  //   1. OPTIMISTIC: immediately patch the matching chore in `board.chores` to
  //      the expected post-action state (e.g. claim → assigneeUserId=me,
  //      assignmentKind='claimed', claimedAt=now ISO). The card re-renders at
  //      once. We do NOT recompute dueness/decay (M5/M6) — `colorTier`/`dueState`
  //      stay as the server last reported until the authoritative response lands.
  //   2. SUCCESS: the server returns the updated `ChoreDto` (with the NEW xmin
  //      `version`). Replace the chore with it — the next mutation then carries a
  //      fresh version (avoids a self-inflicted 409). The authoritative
  //      tier/dueness come from THIS DTO, never a client recompute.
  //   3. 409 (ApiError.isConflict — the retryable xmin conflict): someone else
  //      changed the chore. Roll back the optimistic patch and REFETCH the board
  //      so the user sees the true state (e.g. claimed by someone else). We do
  //      NOT silently retry — that could clobber the other user's claim (MN8
  //      spirit). A brief "someone got there first — refreshed" notice explains it.
  //   4. OTHER 4xx (ApiError.isClientRejection — non-retryable: validation, an
  //      illegal transition, or the WP-08 empty-400-that-is-really-404): roll
  //      back, refetch to resync, surface a non-alarming toast. Not a crash.
  //   5. NETWORK / 5xx: roll back + error toast; the action can be retried.
  // ─────────────────────────────────────────────────────────────────────────

  /** Wire the board refetch (App.svelte's loader). Used for 409/4xx reconcile. */
  setRefresh(refresh: () => Promise<void>): void {
    this.refresh = refresh;
  }

  /**
   * Public board refetch for the room-admin surface (RoomEditSheet + the inline
   * new-room photo). A room create/edit/photo change is not a chore mutation, so
   * it has no optimistic path — after it succeeds we refetch the ONE board
   * payload so the updated rollup (name / icon / photoPath cover) renders
   * authoritatively. Shares the same loader as liveness + 409 reconcile.
   */
  async reloadBoard(): Promise<void> {
    await this.reconcile();
  }

  /** Is a mutation in flight for this chore? (disable its controls). */
  isPending(choreId: number): boolean {
    return this.pendingChoreIds.has(choreId);
  }

  private markPending(choreId: number, on: boolean): void {
    const next = new Set(this.pendingChoreIds);
    if (on) next.add(choreId);
    else next.delete(choreId);
    this.pendingChoreIds = next;
  }

  /** Find a chore by id in the current board (null if the board is unloaded). */
  private choreById(choreId: number): ChoreDto | null {
    return this.board?.chores.find((c) => c.id === choreId) ?? null;
  }

  /** Replace the chore in `board.chores` with the authoritative server DTO. */
  private applyChore(updated: ChoreDto): void {
    const board = this.board;
    if (!board) return;
    const idx = board.chores.findIndex((c) => c.id === updated.id);
    if (idx >= 0) {
      board.chores[idx] = updated;
    }
  }

  private async reconcile(): Promise<void> {
    if (this.refresh) await this.refresh();
  }

  /** Is this chore a multi-person (co-sign) chore per the CURRENT board? */
  private isMultiPerson(choreId: number): boolean {
    return (this.choreById(choreId)?.requiredCount ?? 1) > 1;
  }

  /**
   * Mutation runner for MULTI-PERSON (`requiredCount > 1`) chores. Unlike
   * `runOptimistic`, it applies NO optimistic patch: a single co-sign
   * (claim / drop / hand-off / partial complete) does NOT change the card to a
   * "done" state, and — critically — the single-chore mutation RESPONSE carries
   * `completedCount = 0` for the co-sign progress (the board GET is the only
   * authoritative source, see WP-07). So we fire the call, then `reconcile()`
   * (full board refetch) so the card reflects the true current-occurrence
   * progress (or drops off / resets if the count was just satisfied). The 409 /
   * other-4xx / network branches mirror `runOptimistic` exactly — refetch +
   * surface, never an auto-retry.
   *
   * The returned DTO is still applied first so the version stays fresh even in
   * the (rare) window before the reconcile lands; the reconcile then overwrites
   * the whole board with authoritative progress.
   */
  private async runMultiPersonMutation(
    choreId: number,
    call: (chore: ChoreDto) => Promise<ChoreDto>,
    conflictMessage: string,
  ): Promise<void> {
    if (this.pendingChoreIds.has(choreId)) return;
    const chore = this.choreById(choreId);
    if (!chore) return;

    const snapshot: ChoreDto = { ...chore };
    this.markPending(choreId, true);
    try {
      const updated = await call(snapshot);
      this.applyChore(updated);
      // Authoritative co-sign progress comes only from the board GET (the
      // mutation response can't signal "X of N" — it returns completedCount=0).
      await this.reconcile();
    } catch (e) {
      if (e instanceof ApiError && e.isConflict) {
        await this.reconcile();
        showToast({ message: conflictMessage, kind: 'info' });
      } else if (e instanceof ApiError && e.isClientRejection) {
        // Incl. the D6 distinctness rejection (a non-409 4xx). Resync + notice.
        await this.reconcile();
        showToast({
          message: "That didn't go through — the board has been refreshed.",
          kind: 'info',
        });
      } else {
        showToast({ message: 'Something went wrong. Please try again.', kind: 'error' });
      }
    } finally {
      this.markPending(choreId, false);
    }
  }

  /**
   * Core optimistic runner. Snapshots the chore, applies an optimistic patch,
   * fires the request, then commits the returned DTO or rolls back + reconciles
   * per the 409 / 4xx / network model documented above.
   *
   * @param choreId   the target chore
   * @param patch     fields to optimistically merge onto the chore
   * @param call      the API call (returns the authoritative ChoreDto)
   * @param conflictMessage  the "someone got there first" notice for a 409
   */
  private async runOptimistic(
    choreId: number,
    patch: Partial<ChoreDto>,
    call: (chore: ChoreDto) => Promise<ChoreDto>,
    conflictMessage: string,
  ): Promise<void> {
    // Double-submit guard: ignore if a mutation is already in flight.
    if (this.pendingChoreIds.has(choreId)) return;
    const chore = this.choreById(choreId);
    if (!chore) return;

    // Snapshot the fields we touch so we can roll back precisely.
    const snapshot: ChoreDto = { ...chore };
    Object.assign(chore, patch);
    this.markPending(choreId, true);

    try {
      const updated = await call(snapshot);
      this.applyChore(updated);
    } catch (e) {
      // Roll back the optimistic patch first, regardless of failure mode.
      const current = this.choreById(choreId);
      if (current) Object.assign(current, snapshot);

      if (e instanceof ApiError && e.isConflict) {
        // 409 — xmin concurrency conflict (M7/M12). Refetch + non-alarming notice.
        await this.reconcile();
        showToast({ message: conflictMessage, kind: 'info' });
      } else if (e instanceof ApiError && e.isClientRejection) {
        // Other 4xx (incl. the empty-400-from-404 WP-08 quirk). Resync + notice.
        await this.reconcile();
        showToast({
          message: "That didn't go through — the board has been refreshed.",
          kind: 'info',
        });
      } else {
        // Network / 5xx — leave the board as-is (rolled back), allow retry.
        showToast({
          message: 'Something went wrong. Please try again.',
          kind: 'error',
        });
      }
    } finally {
      this.markPending(choreId, false);
    }
  }

  /** Claim an unclaimed (or stale-claimed) pile chore for the current user. */
  async claim(choreId: number): Promise<void> {
    const me = this.currentUserId;
    const call = (c: ChoreDto) => claimChore(c.id, c.version);
    // Multi-person: reconcile after so the card never shows a stale
    // `completedCount=0` from the single-chore response (WP-07). Assignment
    // isn't co-sign progress, but the response carries no live progress at all,
    // so we re-read the board for truth. Single-person path untouched.
    if (this.isMultiPerson(choreId)) {
      await this.runMultiPersonMutation(choreId, call, 'Someone got there first — board refreshed.');
      return;
    }
    await this.runOptimistic(
      choreId,
      {
        assigneeUserId: me,
        assignmentKind: 'claimed',
        claimedAt: new Date().toISOString(),
        isClaimStale: false,
      },
      call,
      'Someone got there first — board refreshed.',
    );
  }

  /**
   * Take a chore currently held by another member — grab it as a self-claim
   * ("covering" for someone out/sick). The server displaces the holder and
   * lands a Claimed (NOT a sticky Assigned), so a recurring chore returns to the
   * pile after the taker completes it. Optimistic end-state is the same as claim.
   */
  async take(choreId: number): Promise<void> {
    const me = this.currentUserId;
    const call = (c: ChoreDto) => takeChore(c.id, c.version);
    if (this.isMultiPerson(choreId)) {
      await this.runMultiPersonMutation(choreId, call, 'That chore changed — board refreshed.');
      return;
    }
    await this.runOptimistic(
      choreId,
      {
        assigneeUserId: me,
        assignmentKind: 'claimed',
        claimedAt: new Date().toISOString(),
        isClaimStale: false,
      },
      call,
      'That chore changed — board refreshed.',
    );
  }

  /** Drop a chore the current user holds (returns it to the pile). */
  async drop(choreId: number): Promise<void> {
    const call = (c: ChoreDto) => dropChore(c.id, c.version);
    if (this.isMultiPerson(choreId)) {
      await this.runMultiPersonMutation(choreId, call, 'That chore changed — board refreshed.');
      return;
    }
    await this.runOptimistic(
      choreId,
      { assigneeUserId: null, assignmentKind: 'none', claimedAt: null, isClaimStale: false },
      call,
      'That chore changed — board refreshed.',
    );
  }

  /**
   * Mark a chore done. `extra` may carry a note / already-uploaded photo path
   * and — for multi-person (co-sign) chores — the `participantUserIds` recorded
   * via the complete dialog (WP-07). The `api.ts` CompleteRequest already has
   * the field; we pass it straight through.
   *
   * SINGLE-PERSON (`requiredCount == 1`): the EXISTING optimistic path —
   * stamp `lastCompletedAt` then commit the returned DTO. Behaviorally identical
   * to before this WP.
   *
   * MULTI-PERSON (`requiredCount > 1`): no optimistic `{ lastCompletedAt }`
   * patch (a partial co-sign is NOT "done"); fire then reconcile so the card
   * shows the authoritative "X of N" (or drops off when the count is satisfied).
   * The mutation response can't signal progress (returns completedCount=0), so
   * the board refetch is the only correct source — see WP-07.
   */
  async complete(
    choreId: number,
    extra?: { note?: string | null; photoPath?: string | null; participantUserIds?: number[] },
  ): Promise<void> {
    const call = (c: ChoreDto) =>
      completeChore(c.id, {
        note: extra?.note ?? null,
        photoPath: extra?.photoPath ?? null,
        participantUserIds: extra?.participantUserIds,
        version: c.version,
      } satisfies CompleteRequest);

    if (this.isMultiPerson(choreId)) {
      await this.runMultiPersonMutation(choreId, call, 'That chore changed — board refreshed.');
    } else {
      await this.runOptimistic(
        choreId,
        // Optimistically reflect "just completed" by stamping lastCompletedAt.
        // We do NOT recompute dueness/colorTier — the authoritative post-completion
        // tier (e.g. recurring chore's next dueness) comes from the returned DTO.
        { lastCompletedAt: new Date().toISOString() },
        call,
        'That chore changed — board refreshed.',
      );
    }
    // A completion shifts the distribution — invalidate the cached equity so it
    // reloads when next viewed (or now, if the equity lens is active). The
    // single-person happy path doesn't refetch the board, so this is its only
    // equity hook; the multi-person path already reconciled via setBoard (which
    // also invalidates), and a 409/4xx reconciled the same way.
    this.invalidateEquity();
  }

  // ── Roster (multi-person named soft roster, rework) ───────────────────────
  // All three are multi-person-only and use the runMultiPersonMutation path
  // (fire → reconcile via the board GET; never auto-retry on 409/4xx — MN4).

  /**
   * Commit the current user to a multi-person chore's roster ("I'm in" — self-opt-in or confirming an
   * assignment). The single-chore response carries an empty roster, so we reconcile via the board GET.
   */
  async commit(choreId: number): Promise<void> {
    await this.runMultiPersonMutation(
      choreId,
      (c) => commitRoster(c.id, c.version),
      'That chore changed — board refreshed.',
    );
  }

  /** Leave a multi-person chore's roster (decline / "not me"). */
  async leave(choreId: number): Promise<void> {
    await this.runMultiPersonMutation(
      choreId,
      (c) => leaveRoster(c.id, null, c.version),
      'That chore changed — board refreshed.',
    );
  }

  /** Assign a named member to a multi-person chore's roster (Assigned — from the edit sheet). */
  async assign(choreId: number, subjectUserId: number): Promise<void> {
    await this.runMultiPersonMutation(
      choreId,
      (c) => assignRoster(c.id, subjectUserId, c.version),
      'That chore changed — board refreshed.',
    );
  }

  /**
   * Remove a NAMED member from a multi-person chore's roster. Server-guarded:
   * only the chore's creator or owner may remove someone else (anyone may remove
   * themselves via `leave`). A rejected remove reconciles + surfaces a notice.
   */
  async removeFromRoster(choreId: number, subjectUserId: number): Promise<void> {
    await this.runMultiPersonMutation(
      choreId,
      (c) => leaveRoster(c.id, subjectUserId, c.version),
      'That chore changed — board refreshed.',
    );
  }

  /**
   * Reconcile a multi-person chore's named roster to a chosen member set (the
   * edit sheet's "Assign people"). Diffs `selectedUserIds` against
   * `originalRoster` (the roster captured at edit time): assigns anyone newly
   * selected, removes anyone deselected who is still assigned/in. DONE members
   * are never removed — their contribution stands. Runs sequentially so each op
   * carries a fresh version (every roster mutation reconciles the board). Adding
   * is unguarded; removing OTHERS is creator/owner-only server-side, so a rejected
   * remove just surfaces a gentle notice and the board reconciles to the truth.
   */
  async applyRosterSelection(
    choreId: number,
    originalRoster: { userId: number; state: RosterState }[],
    selectedUserIds: number[],
  ): Promise<void> {
    const selected = new Set(selectedUserIds);
    const currentIds = new Set(originalRoster.map((m) => m.userId));
    const toAdd = selectedUserIds.filter((id) => !currentIds.has(id));
    const toRemove = originalRoster
      .filter((m) => m.state !== 'done' && !selected.has(m.userId))
      .map((m) => m.userId);
    for (const id of toAdd) {
      await this.assign(choreId, id);
    }
    for (const id of toRemove) {
      await this.removeFromRoster(choreId, id);
    }
  }

  /**
   * Hand a chore off to another member, or back to the pile (targetUserId null).
   */
  async handOff(choreId: number, targetUserId: number | null): Promise<void> {
    const toPile = targetUserId == null;
    const call = (c: ChoreDto) => handOffChore(c.id, { targetUserId, version: c.version });
    if (this.isMultiPerson(choreId)) {
      await this.runMultiPersonMutation(choreId, call, 'That chore changed — board refreshed.');
      return;
    }
    await this.runOptimistic(
      choreId,
      toPile
        ? { assigneeUserId: null, assignmentKind: 'none', claimedAt: null, isClaimStale: false }
        : {
            // Hand-off to a member is a deliberate Assigned server-side
            // (SetAssigned) — reflect that optimistically so the taker doesn't
            // see a "Drop" flash (Drop is Claimed-only) before the board GET.
            assigneeUserId: targetUserId,
            assignmentKind: 'assigned',
            claimedAt: new Date().toISOString(),
            isClaimStale: false,
          },
      call,
      'That chore changed — board refreshed.',
    );
  }

  /**
   * Create a chore (quick-add). NOT optimistic — a new chore has no id/version
   * to reconcile against and the server-computed dueness/rollups are needed, so
   * we POST then refetch the whole board to render the new card authoritatively.
   * Returns the created chore so the caller can chain a photo upload if needed.
   * Throws on failure so the dialog can keep itself open + surface the error.
   */
  async create(body: CreateChoreRequest): Promise<ChoreDto> {
    const created = await createChore(body);
    await this.reconcile();
    return created;
  }

  /**
   * Edit a chore's metadata (name, description, room, recurrence, effort, owner,
   * photoPath) via PUT /api/chores/{id}. Optimistic — applies the expected updates
   * at once and reconciles on failure.
   *
   * Assignment is NOT editable here (v1.0 D6 — moves only via claim/handoff).
   * No client date math (MN9) — anchorDate/daysOfWeek/intervalDays come straight
   * from the caller (mapped in EditChoreSheet).
   *
   * On 409 (xmin conflict): roll back + reconcile + info toast.
   * On other 4xx: roll back + reconcile + info toast.
   * On network/5xx: roll back + error toast.
   *
   * The reconcile (loadBoard) path calls setBoard → invalidateEquity, so equity
   * cache invalidation is handled automatically there. On the happy path the
   * returned DTO replaces the chore in-place; equity is also invalidated (a name/
   * room/recurrence change could affect the board the digest reads, even if the
   * equity endpoint caches on completions).
   */
  async edit(choreId: number, body: UpdateChoreRequest): Promise<void> {
    // Optimistic patch: apply the editable fields we can preview before the
    // round-trip. We intentionally don't touch assignee/assignment fields (D6).
    const patch: Partial<ChoreDto> = {
      name: body.name,
      description: body.description ?? null,
      roomId: body.roomId ?? null,
      recurrenceMode: body.recurrenceMode,
      effortTier: body.effortTier,
      ownerUserId: body.ownerUserId ?? null,
    };
    await this.runOptimistic(
      choreId,
      patch,
      (_snapshot) => updateChore(choreId, body),
      'Someone edited that chore at the same time — board refreshed.',
    );
    // On the happy path `runOptimistic` replaced the chore with the returned DTO
    // but did NOT call setBoard, so invalidate equity manually here.
    this.invalidateEquity();
  }

  /**
   * Seed the household with the starter chore set (POST /api/chores/seed-starter).
   * Idempotent server-side — safe to call repeatedly; `seeded: false` if already done.
   * On success: refetch the board (which calls setBoard → invalidateEquity) + success toast.
   * On failure: error toast (no board mutation to roll back).
   */
  async seedStarter(): Promise<void> {
    try {
      const result = await apiSeedStarter();
      // Refetch the board so new chores appear. setBoard calls invalidateEquity.
      await this.reconcile();
      if (result.seeded) {
        showToast({ message: 'Starter chores added.', kind: 'success' });
      }
      // seeded=false is idempotent (already had chores) — refetch is still useful
      // to keep the board fresh; no toast so there's no noise.
    } catch {
      showToast({ message: "Couldn't load starter chores. Please try again.", kind: 'error' });
    }
  }

  /** Delete a chore (optimistic removal + rollback/refetch on failure). */
  async remove(choreId: number): Promise<void> {
    if (this.pendingChoreIds.has(choreId)) return;
    const board = this.board;
    if (!board) return;
    const idx = board.chores.findIndex((c) => c.id === choreId);
    if (idx < 0) return;
    const removed = board.chores[idx];

    board.chores = board.chores.filter((c) => c.id !== choreId);
    this.markPending(choreId, true);
    try {
      await deleteChore(choreId, removed.version);
    } catch (e) {
      // Restore the removed chore, then reconcile / surface.
      await this.reconcile();
      if (e instanceof ApiError && (e.isConflict || e.isClientRejection)) {
        showToast({ message: 'That chore changed — board refreshed.', kind: 'info' });
      } else {
        // Network/5xx: the refetch may not have restored it; put it back.
        if (this.board && !this.board.chores.some((c) => c.id === choreId)) {
          this.board.chores = [...this.board.chores, removed];
        }
        showToast({ message: 'Something went wrong. Please try again.', kind: 'error' });
      }
    } finally {
      this.markPending(choreId, false);
    }
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

/**
 * Resolve a room rollup (name + icon) for a chore's roomId, for the per-card
 * room locator chip. null when the chore is roomless (the virtual General group)
 * or the roomId is unknown — callers render no chip in that case.
 */
export function roomFor(roomId: number | null | undefined): RoomRollupDto | null {
  if (roomId == null) return null;
  return boardStore.roomsById.get(roomId) ?? null;
}

/**
 * Coerce a persisted `userDefaultView` string to a canonical `ChoreLensId`.
 * null/blank/unknown ⇒ null (⇒ Needs-attention default). The server allowlist
 * is authoritative (ChoreLens.All), but we re-validate defensively so an
 * unexpected value never selects a non-existent lens.
 */
function coerceLens(view: string | null): ChoreLensId | null {
  if (!view) return null;
  return (CHORE_LENSES as readonly string[]).includes(view) ? (view as ChoreLensId) : null;
}
