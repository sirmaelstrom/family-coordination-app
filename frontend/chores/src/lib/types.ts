// ─────────────────────────────────────────────────────────────────────────
// FROZEN board DTO contract — mirrors the WP-05 board read model EXACTLY.
// Source of truth: tests/FamilyCoordinationApp.Tests/Fixtures/ChoreBoard/board.json
// (M9 consumer-audit tripwire: a DTO shape change updates THIS interface AND
//  that fixture in lockstep). JSON keys are camelCase.
//
// ⚠ CASING GOTCHA (load-bearing — secondary risk is island/DTO drift):
//   - `dueState`, `colorTier`, `assignmentKind`, rollup `status` are real C#
//     enums serialized via JsonStringEnumConverter(CamelCase) → camelCase
//     string unions below.
//   - `recurrenceMode` and `effortTier` are stored on the DTO as PLAIN strings
//     (entity-enum `.ToString()`, NOT routed through the converter) → they
//     serialize PascalCase. Their TS unions below MUST be PascalCase.
// ─────────────────────────────────────────────────────────────────────────

/** PascalCase — plain string on the DTO (NOT enum-converted). */
export type RecurrenceMode = 'OneOff' | 'Fixed' | 'Flexible';

/** PascalCase — plain string on the DTO (NOT enum-converted). */
export type EffortTier = 'Quick' | 'Standard' | 'BigJob';

/** camelCase — enum via JsonStringEnumConverter(CamelCase). Server-computed dueness. */
export type DueState = 'notDue' | 'dueToday' | 'overdue' | 'scheduled';

/** camelCase — enum via JsonStringEnumConverter(CamelCase). Server-computed decay tier. */
export type ColorTier = 'fresh' | 'mid' | 'due' | 'overdue';

/** camelCase — enum via JsonStringEnumConverter(CamelCase). */
export type AssignmentKind = 'none' | 'assigned' | 'claimed';

/** camelCase — enum via JsonStringEnumConverter(CamelCase). Room rollup dirtiness. */
export type RoomRollupStatus = 'clean' | 'attention' | 'needsWork';

export interface ChoreDto {
  id: number;
  name: string;
  description: string | null;
  roomId: number | null;
  recurrenceMode: RecurrenceMode;
  /** Server-computed — NEVER recompute dueness client-side (M5/M6). */
  dueState: DueState;
  /** Server-computed — NEVER recompute the decay tier client-side (M5/M6). */
  colorTier: ColorTier;
  /** ISO-8601 UTC (Z). Server-computed; render only — do not derive dueness from it. */
  nextDueAt: string | null;
  isClaimStale: boolean;
  effortTier: EffortTier;
  effortPoints: number;
  ownerUserId: number | null;
  assigneeUserId: number | null;
  assignmentKind: AssignmentKind;
  claimedAt: string | null;
  lastCompletedAt: string | null;
  photoPath: string | null;
  /** xmin optimistic-concurrency token. Echo back on mutations (WP-11). */
  version: number;
}

export interface RoomRollupDto {
  /** null = the virtual "General" group (roomless chores). */
  roomId: number | null;
  name: string;
  icon: string;
  photoPath: string | null;
  /** General sorts last (int.MaxValue = 2147483647). */
  sortOrder: number;
  choreCount: number;
  dueCount: number;
  status: RoomRollupStatus;
}

export interface MemberDto {
  userId: number;
  displayName: string;
  initials: string;
  pictureUrl: string | null;
}

export interface ChoreBoardDto {
  chores: ChoreDto[];
  rooms: RoomRollupDto[];
  members: MemberDto[];
  /** Overdue/dueToday OR unclaimed pile, server-ordered. */
  needsAttentionChoreIds: number[];
  /** Persisted lens id; null ⇒ Needs-attention. One of ChoreLens (see below). */
  userDefaultView: string | null;
}

// ─── Rooms (admin surface, /api/rooms) ──────────────────────────────────────

export interface RoomDto {
  id: number;
  name: string;
  icon: string;
  photoPath: string | null;
  sortOrder: number;
}

// ─── Canonical lens ids (M-canonical-lens / council M6) ─────────────────────
// Must match the C# ChoreLens.All allowlist used by PATCH /me/default-view.
// No ad-hoc casings. WP-10/12 switch lenses against these against the ONE
// board payload (client-side grouping; no per-lens endpoint — M11).

export type ChoreLensId =
  | 'needs-attention'
  | 'rooms'
  | 'up-for-grabs'
  | 'mine'
  | 'equity';

export const CHORE_LENSES: readonly ChoreLensId[] = [
  'needs-attention',
  'rooms',
  'up-for-grabs',
  'mine',
  'equity',
] as const;

// ─── Equity lens DTO (FROZEN — mirrors WP-02 ChoreEquityDto EXACTLY) ─────────
// Source of truth: tests/FamilyCoordinationApp.Tests/Fixtures/ChoreEquity/equity.json
// (M7 lockstep tripwire: a shape change updates THIS interface AND that fixture).
// Served at GET /api/chores/equity?window=week|all. JSON keys are camelCase.
//
// ⚠ `window` is a PLAIN string on the DTO ('week'|'all'). `sharePct` /
//   `equalSharePct` are PERCENT values in 0..100 (e.g. 41.7) — render them
//   DIRECTLY as a percent (no client `* 100`). All values are server-computed;
//   render only — NO client date math (MN9), no leaderboard/ranking framing (M12).

/** The equity reporting window. Plain string on the DTO. */
export type EquityWindow = 'week' | 'all';

/** Per-member share of the household's completion load over the window. */
export interface MemberShareDto {
  userId: number;
  displayName: string;
  initials: string;
  pictureUrl: string | null;
  points: number;
  completions: number;
  /** PERCENT 0..100 (e.g. 41.7). Render directly — no client `* 100`. */
  sharePct: number;
}

export interface ChoreEquityDto {
  window: EquityWindow;
  totalPoints: number;
  totalCompletions: number;
  /** PERCENT 0..100 — the neutral equal-share reference line. */
  equalSharePct: number;
  fallingBehindCount: number;
  upForGrabsCount: number;
  members: MemberShareDto[];
}

// ─── Shell context (read from the #chores-root data-attributes) ─────────────

export interface ShellContext {
  householdId: number;
  userId: number;
  userName: string;
}
