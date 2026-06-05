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

/**
 * camelCase — enum via JsonStringEnumConverter(CamelCase). A member's DERIVED state on a multi-person
 * chore's named roster (rework): `assigned` = suggested (a pre-opt-in, no reply); `in` = committed
 * ("I'm in"); `done` = completed their part this occurrence.
 */
export type RosterState = 'assigned' | 'in' | 'done';

/** One member of a multi-person chore's derived roster. */
export interface RosterMemberDto {
  userId: number;
  state: RosterState;
}

/** camelCase — enum via JsonStringEnumConverter(CamelCase). Room rollup dirtiness. */
export type RoomRollupStatus = 'clean' | 'attention' | 'needsWork';

export interface ChoreDto {
  id: number;
  name: string;
  /** Optional emoji/short-code icon (parity with room icons); "" = none. */
  icon: string;
  description: string | null;
  roomId: number | null;
  recurrenceMode: RecurrenceMode;
  /** Recurrence sub-value echoed for edit pre-fill (Flexible "every N days"); null otherwise. */
  intervalDays: number | null;
  /** camelCase CSV of weekday flags (e.g. "monday, thursday") for Fixed chores; null otherwise. */
  daysOfWeek: string | null;
  /** ISO date "YYYY-MM-DD" — a one-off chore's due date (maps to anchorDate); null otherwise. */
  anchorDate: string | null;
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
  /** 1 = normal chore; >1 = multi-person (named roster). Always ≥ 1. */
  requiredCount: number;
  /** Distinct members DONE toward the CURRENT open occurrence (0..requiredCount) — the gate. Board GET only. */
  completedCount: number;
  /** Named roster + per-member state (assigned/in/done), ascending by userId. [] = open / single-person. */
  roster: RosterMemberDto[];
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

// ─── Digest settings (WP-11 — mirrors WP-06 frozen contract EXACTLY) ─────────
//
// Wire casing: camelCase enum strings via JsonStringEnumConverter(CamelCase).
// The GET never returns the webhook URL (MN7) — only a hasWebhook flag + hint.
// The PUT tri-state webhookAction drives whether the secret is kept/replaced/cleared.

/** camelCase — enum via JsonStringEnumConverter(CamelCase). Only 'weekly' for now; union-extensible. */
export type DigestCadence = 'weekly';

/** camelCase — enum via JsonStringEnumConverter(CamelCase). */
export type DigestDay =
  | 'sunday'
  | 'monday'
  | 'tuesday'
  | 'wednesday'
  | 'thursday'
  | 'friday'
  | 'saturday';

/**
 * Tri-state webhook action for PUT /api/chores/digest-settings.
 * 'keep' = leave stored URL unchanged (default when input untouched).
 * 'set'  = replace with the provided webhookUrl.
 * 'clear' = remove the stored URL.
 */
export type WebhookAction = 'keep' | 'set' | 'clear';

/**
 * Safe view returned by GET /api/chores/digest-settings.
 * ⚠ The webhook URL is NEVER in this response (MN7) — only hasWebhook + a
 * masked hint. Do NOT fetch or render the stored URL anywhere client-side.
 */
export interface DigestSettingsView {
  enabled: boolean;
  cadence: DigestCadence;
  sendDayOfWeek: DigestDay;
  sendHourLocal: number;
  /** True when a webhook URL is stored (encrypted at rest). */
  hasWebhook: boolean;
  /** A masked hint (e.g. "…xyz") to help the user identify the stored URL; null when no webhook. */
  webhookHint: string | null;
  /** ISO-8601 UTC timestamp of the last sent digest; null when never sent. */
  lastSentAt: string | null;
}

/**
 * PUT body for /api/chores/digest-settings.
 * webhookAction controls the tri-state: 'keep' (default), 'set' (+ webhookUrl), 'clear'.
 * ⚠ Never log webhookUrl client-side; never put it in a query string (MN7).
 */
export interface DigestSettingsUpdate {
  enabled: boolean;
  cadence: DigestCadence;
  sendDayOfWeek: DigestDay;
  sendHourLocal: number;
  webhookAction: WebhookAction;
  /** Required when webhookAction is 'set'. Not sent otherwise. */
  webhookUrl?: string;
}

// ─── Shell context (read from the #chores-root data-attributes) ─────────────

export interface ShellContext {
  householdId: number;
  userId: number;
  userName: string;
}
