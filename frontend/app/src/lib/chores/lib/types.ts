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

/**
 * A lightweight per-chore checklist item (Phase 14). Versionless / last-write-wins (no concurrency token).
 * A momentum aid only — never gates completion; resets on a recurring chore's satisfying completion.
 */
export interface ChoreSubtaskDto {
  id: number;
  title: string;
  isDone: boolean;
  sortOrder: number;
  /** "Who ticked it" actor — non-null IFF isDone (per-occurrence). Resolve via membersById/memberFor. */
  completedByUserId: number | null;
  /** ISO-8601 UTC (Z) when it was ticked; null when not done. Server-stamped — build no Date client-side. */
  completedAt: string | null;
}

export interface ChoreDto {
  id: number;
  name: string;
  /** Optional emoji/short-code icon (parity with room icons); "" = none. */
  icon: string;
  description: string | null;
  /** Phase 13: the chore's 0..N room memberships (was a single roomId). [] = General. Sorted ascending. */
  roomIds: number[];
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
  /** ISO date "YYYY-MM-DD" — the snooze / set-next-due floor; null = no floor. For the edit-sheet pre-fill. */
  snoozedUntil: string | null;
  /** Server-computed gate (today < snoozedUntil). The filter + Home read THIS — never date-math snoozedUntil. */
  isSnoozed: boolean;
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
  /** Per-chore checklist (Phase 14); momentum aid, never gates completion. Ordered by sortOrder; [] = none. */
  subtasks: ChoreSubtaskDto[];
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
  /** Persisted lens id; null ⇒ Up-for-grabs. One of ChoreLens (see below). */
  userDefaultView: string | null;
  /**
   * The REQUESTING user's OWN physical-capacity tier (Phase 15 R4′); `null` ⇒ Full (the pre-migration
   * default). Rides the board payload so the up-for-grabs "Fits me" chip reads the caller's tier without the
   * separately-cached equity fetch. Serializes AFTER `userDefaultView` (additive init-only body property).
   */
  callerCapacityTier: CapacityTier | null;
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
// Must match the C# ChoreLens.All allowlist used by PATCH /me/default-view —
// the server validates the persisted default against these EXACT ids, so none
// may be removed. No ad-hoc casings. Switching groups the ONE board payload
// client-side (no per-lens endpoint — M11).
//
// Model A board IA (Phase 14) reinterprets them in the island: the first three
// are PRIMARY board filters (up-for-grabs / mine / needs-attention="All") over a
// single attention-sectioned board; rooms / equity are on-demand ORGANIZERS. The
// ViewSwitcher defines the grouping/labels explicitly, so this order is just for
// tidiness — it is NOT load-bearing.

export type ChoreLensId =
  | 'up-for-grabs'
  | 'mine'
  | 'needs-attention'
  | 'rooms'
  | 'equity'
  | 'recap';

export const CHORE_LENSES: readonly ChoreLensId[] = [
  'up-for-grabs',
  'mine',
  'needs-attention',
  'rooms',
  'equity',
  'recap',
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

/**
 * A member's physical-capacity tier (Phase 15). Mirrors the C# `CapacityTier.All` casing EXACTLY
 * (PascalCase strings — NOT enum-converted). `null` on the DTO ⇒ Full (the pre-migration default).
 */
export type CapacityTier = 'Full' | 'Reduced' | 'Minimal';

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
  /**
   * The member's capacity-WEIGHTED expected share (PERCENT 0..100, UNROUNDED — format for display; Phase 15
   * WP-05). The island draws THIS as each member's per-member reference line instead of the single flat
   * equal-share marker. `sharePct` stays the RAW actual bar. Render directly — no client `* 100`.
   */
  expectedSharePct: number;
}

/**
 * Per-member planning/coordination footprint (Phase 15). ALL-TIME, un-blended labeled tallies — the
 * server computes them all-time regardless of `window` (D5). Plain integer COUNTS (no percent, no weight);
 * NEVER summed into a blended score (MN4). Mirrors the C# `MemberPlanningDto` and the `planning` array in
 * equity.json EXACTLY (M5 lockstep).
 */
export interface MemberPlanningDto {
  userId: number;
  displayName: string;
  choresSetUp: number;
  recipesAdded: number;
  listItemsCurated: number;
  handOffs: number;
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
  /**
   * All-time planning footprint per member (Phase 15). Always present (defaults to [] server-side) —
   * INDEPENDENT of `window`. Render as neutral labeled tallies; never sum/blend with physical points (MN4).
   */
  planning: MemberPlanningDto[];
  /**
   * The REQUESTING user's own physical-capacity tier (Phase 15 WP-05, P4); `null` ⇒ Full (the pre-migration
   * default). Rides the equity payload so the self-only capacity selector reflects state without a separate
   * GET (M7). The selector writes via PATCH /me/capacity (caller-scoped only — MN5).
   */
  callerCapacityTier: CapacityTier | null;
}

// ─── Weekly recap lens DTO (mirrors ChoreRecapDtos.cs EXACTLY) ───────────────
// Served at GET /api/chores/recap?weeks=N. JSON keys are camelCase. The `current`
// week is the SAME content the Discord digest posts (same headline/distribution/
// falling-behind/up-for-grabs). `trend` is week-over-week totals, oldest→newest.
//
// ⚠ MN9: `weekStartLocal` is a date-only string ("YYYY-MM-DD") already resolved in
//   the household timezone server-side. NEVER `new Date('YYYY-MM-DD')` on it — render
//   the string, or parse parts manually. `sharePct` is PERCENT 0..100 (render direct).

/** One member's line in the current-week distribution (no userId/mention — neutral). */
export interface RecapMemberLineDto {
  displayName: string;
  points: number;
  /** PERCENT 0..100 (e.g. 41.7). Render directly — no client `* 100`. */
  sharePct: number;
}

/** The current week's assembled recap — identical to the Discord digest content. */
export interface RecapWeekDto {
  /** Local Monday of the week, "YYYY-MM-DD" (household tz; MN9 — do not Date-parse). */
  weekStartLocal: string;
  /** Collective, non-punitive headline. */
  headline: string;
  totalCompletions: number;
  totalPoints: number;
  distribution: RecapMemberLineDto[];
  /** Names of chores Overdue/DueToday (attention list, not blame). */
  fallingBehind: string[];
  upForGrabsCount: number;
}

/**
 * One week's totals in the week-over-week trend, plus its per-member `distribution` (Phase 15). The
 * distribution is a WITHIN-week breakdown (displayName only) rendered on a SELECTED week — NEVER keyed as a
 * single person's line across weeks (MN2: that rebuilds the dropped "B" scoreboard).
 */
export interface RecapTrendPointDto {
  /** Local Monday of the week, "YYYY-MM-DD" (household tz; MN9 — do not Date-parse). */
  weekStartLocal: string;
  totalCompletions: number;
  totalPoints: number;
  /** True for the in-progress current week (the last, partial bar). */
  isCurrent: boolean;
  /** Per-member effort split for THIS week (displayName only; sums to the week total). */
  distribution: RecapMemberLineDto[];
}

/** The highest-output week in the window (Phase 15). Uses total* names to match the sibling recap DTOs. */
export interface BestWeekDto {
  weekStartLocal: string;
  totalCompletions: number;
  totalPoints: number;
}

/** A chore whose ALL-TIME first-ever completion landed in the window ("first time!"). */
export interface FirstEverDto {
  choreName: string;
  /** "YYYY-MM-DD" household-local (MN9 — do not Date-parse). */
  localDate: string;
}

/** The collective milestones over the recap window (Phase 15 — effort/count facts, no per-person ranking). */
export interface MilestonesDto {
  /** null when the window had no activity. */
  bestWeek: BestWeekDto | null;
  longestActiveStreakWeeks: number;
  firstEvers: FirstEverDto[];
  seasonTotalCompletions: number;
  seasonTotalPoints: number;
}

/** A completion that carried a note or photo — the logbook's "kept moments" (newest-first, cap 12). */
export interface KeptMomentDto {
  /** "YYYY-MM-DD" household-local (MN9 — do not Date-parse). */
  localDate: string;
  choreName: string;
  note: string | null;
  hasPhoto: boolean;
}

/** Per-room completion tally over the window. Roomless completions bucket into the virtual "General" group. */
export interface WhatGotTendedDto {
  roomName: string;
  completions: number;
}

/**
 * Full recap payload (Phase 15 — the evolved logbook): the digest-mirror current week + the week-over-week
 * trend (each point now carrying a per-week distribution) + the additive logbook sections. Mirrors
 * ChoreRecapDtos.cs EXACTLY (M5 lockstep with Fixtures/ChoreRecap/recap.json).
 */
export interface ChoreRecapDto {
  current: RecapWeekDto;
  trend: RecapTrendPointDto[];
  milestones: MilestonesDto;
  keptMoments: KeptMomentDto[];
  whatGotTended: WhatGotTendedDto[];
  /** The shared gone-quiet band (same shape + data as the ledger's). */
  goneQuiet: GoneQuietDto[];
}

// ─── Chore-history ledger lens DTO (mirrors ChoreLedgerDtos.cs EXACTLY) ──────
// Served at GET /api/chores/ledger?weeks=N (default 12). JSON keys are camelCase.
// Source of truth: tests/FamilyCoordinationApp.Tests/Fixtures/ChoreHistory/ledger.json
// (M5 lockstep tripwire). displayName ONLY — no userId/mention anywhere (D9/MN1).
//
// ⚠ MN9: every date is a "YYYY-MM-DD" household-local string already resolved
//   server-side. NEVER `new Date('YYYY-MM-DD')` — group by the string (weekLabel()).

/** One completion in the ledger feed (displayName only — neutral framing). */
export interface LedgerEventDto {
  choreName: string;
  doerDisplayName: string;
  /** "YYYY-MM-DD" household-local (MN9 — do not Date-parse). */
  localDate: string;
  points: number;
  note: string | null;
  hasPhoto: boolean;
}

/** One week of the weave scaffold (incl. empty weeks). Per-day density derives client-side from events/ghosts. */
export interface LedgerWeekDto {
  /** Local Monday of the week, "YYYY-MM-DD" (household tz; MN9). */
  weekStartLocal: string;
  completions: number;
}

/** An expected-but-missing beat. `reason` is 'snoozed' | 'slipped' (the server-internal ReasonFromLog is off-wire). */
export interface GhostDto {
  choreName: string;
  /** "YYYY-MM-DD" household-local (MN9 — do not Date-parse). */
  expectedLocalDate: string;
  reason: 'snoozed' | 'slipped';
}

/**
 * A chore that has gone quiet (≥2 trailing missed beats). SHARED by the ledger AND recap payloads — defined
 * ONCE here (mirrors the C# single-owner `GoneQuietDto`). `lastCompletedLocalDate` is `null` when the chore
 * was never completed (the key is always present).
 */
export interface GoneQuietDto {
  choreName: string;
  cadenceLabel: string;
  /** "YYYY-MM-DD" household-local, or null = never completed (MN9 — do not Date-parse). */
  lastCompletedLocalDate: string | null;
  reason: 'snoozed' | 'slipped';
}

/** Full ledger payload: window bounds + completion feed + weave scaffold + ghost rows + gone-quiet band. */
export interface ChoreLedgerDto {
  /** "YYYY-MM-DD" household-local window bounds (the weave grid extent; MN9). */
  windowStartLocal: string;
  windowEndLocal: string;
  events: LedgerEventDto[];
  weeks: LedgerWeekDto[];
  ghosts: GhostDto[];
  goneQuiet: GoneQuietDto[];
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
