using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Dtos;

/// <summary>
/// The single board DTO every island lens groups client-side (D17/D18/M11). There is ONE board shape —
/// needs-attention / rooms / up-for-grabs / mine are client-side groupings of this same payload, not
/// separate endpoints. Serialized camelCase with enums as strings (council M5 — a global
/// <c>JsonStringEnumConverter(JsonNamingPolicy.CamelCase)</c> is registered in WP-06's Program.cs). The
/// checked-in <c>board.json</c> contract fixture pins this exact shape; the island's <c>api.ts</c>
/// interface mirrors it (M9 consumer audit — a field rename breaks the fixture test in lockstep).
/// </summary>
public sealed record ChoreBoardDto(
    IReadOnlyList<ChoreDto> Chores,
    IReadOnlyList<RoomRollupDto> Rooms,
    IReadOnlyList<MemberDto> Members,
    IReadOnlyList<int> NeedsAttentionChoreIds,
    string? UserDefaultView);

/// <summary>
/// A single chore with its <b>computed</b> dueness/freshness state (WP-02). The island renders
/// <see cref="DueState"/> / <see cref="ColorTier"/> directly and does no date math of its own (M5/M6).
/// <see cref="IsClaimStale"/> is computed on read but NOT materialized — a stale claim still displays as
/// Claimed until the next write auto-releases it; the island may treat a stale-Claimed chore as
/// pile-eligible visually (D7/MN1).
/// <para><see cref="IntervalDays"/>, <see cref="DaysOfWeek"/>, and <see cref="AnchorDate"/> (a one-off's due
/// date) echo the recurrence sub-values so the edit sheet can pre-fill them (without them, editing a
/// fixed-weekly / every-N / dated one-off chore lost the existing selection). <see cref="DaysOfWeek"/>
/// serializes as a camelCase CSV (e.g. <c>"monday, thursday"</c>); <see cref="AnchorDate"/> as an ISO date
/// (<c>"2026-06-10"</c>) — the same shapes the write request accepts.</para>
/// <para>Multi-person named roster (rework): <see cref="RequiredCount"/> is always ≥ 1 (1 = normal,
/// &gt;1 = multi-person). <see cref="CompletedCount"/> is the count of distinct members DONE toward the
/// CURRENT open occurrence (0..<see cref="RequiredCount"/> — the satisfaction gate). <see cref="Roster"/>
/// is the derived named roster: each member with state assigned/in/done (ascending by userId). For X&gt;1
/// chores it is folded by <c>ChoreRosterCalculator</c>; for X=1 it is synthesized from the assignment trio
/// (0 or 1 member); these are populated only in the board projection (<c>GetBoardAsync</c>). Single-chore
/// projections report <c>completedCount=0</c> and an X&gt;1 chore's roster empty — the client refetches the
/// board for authoritative roster progress (WP-07).</para>
/// </summary>
public sealed record ChoreDto(
    int Id,
    string Name,
    string Icon,
    string? Description,
    // Phase 13: the chore's 0..N room memberships (was a single int? RoomId). Empty == General. Kept at the
    // 5th positional slot (E2). Sorted ascending server-side (deterministic wire → stable contract fixture).
    IReadOnlyList<int> RoomIds,
    string RecurrenceMode,
    int? IntervalDays,
    ChoreDaysOfWeek? DaysOfWeek,
    DateOnly? AnchorDate,
    DueState DueState,
    ColorTier ColorTier,
    DateTime? NextDueAt,
    // Snooze / set-next-due floor echoed for the edit-sheet pre-fill + the chip (mirrors AnchorDate's ISO-date
    // serialization, e.g. "2026-07-01"; null = no floor). IsSnoozed is the SERVER-computed gate
    // (today < SnoozedUntil), copied from ChoreDuenessResult.IsSnoozed — the single source the island filter +
    // Home count read (M5). The chip binds nextDueAt (the resume date), NOT snoozedUntil (WP-06).
    DateOnly? SnoozedUntil,
    bool IsSnoozed,
    bool IsClaimStale,
    string EffortTier,
    int EffortPoints,
    int? OwnerUserId,
    int? AssigneeUserId,
    AssignmentKind AssignmentKind,
    DateTime? ClaimedAt,
    DateTime? LastCompletedAt,
    string? PhotoPath,
    uint Version,
    int RequiredCount,                    // 1 = normal; >1 = multi-person
    int CompletedCount,                   // distinct DONE toward the CURRENT occurrence (0..RequiredCount) — the gate
    IReadOnlyList<RosterMemberDto> Roster, // named roster + per-member state; [] = open / single-person unassigned
                                           // Lightweight per-chore checklist (Phase 14); a momentum aid that never gates completion; resets on a
                                           // recurring chore's satisfying completion. Last field ⇒ serializes after `roster` (matches the fixture).
    IReadOnlyList<ChoreSubtaskDto> Subtasks
    );

/// <summary>
/// A lightweight checklist item on a chore (Phase 14). Wire shape camelCase: <c>{ id, title, isDone,
/// sortOrder, completedByUserId, completedAt }</c>. Versionless / last-write-wins — there is no concurrency
/// token on this DTO. Embedded as <see cref="ChoreDto.Subtasks"/> on the board payload (Phase-14 Unit #2).
/// <para><see cref="CompletedByUserId"/> / <see cref="CompletedAt"/> are the "who ticked it" actor stamp
/// (Phase-14 follow-up): non-null IFF <see cref="IsDone"/> is true (per-occurrence; cleared on untick + on the
/// recurring reset). The userId resolves to a member client-side via the board's members list (no FK).</para>
/// </summary>
public sealed record ChoreSubtaskDto(int Id, string Title, bool IsDone, int SortOrder, int? CompletedByUserId, DateTime? CompletedAt);

/// <summary>
/// A member's DERIVED state on a multi-person chore's named roster, for the current occurrence (rework).
/// Serialized camelCase via <c>JsonStringEnumConverter(CamelCase)</c>: <c>"assigned" | "in" | "done"</c>.
/// <list type="bullet">
///   <item><see cref="Assigned"/> — suggested (a pre-opt-in by someone else; no reply yet, declinable).</item>
///   <item><see cref="In"/> — committed ("I'm in": self-opt-in, or confirming an assignment).</item>
///   <item><see cref="Done"/> — completed their part this occurrence (overlaid from <c>ChoreCompletion</c>).</item>
/// </list>
/// </summary>
public enum RosterState
{
    Assigned,
    In,
    Done
}

/// <summary>
/// One member of a multi-person chore's derived roster (D10). Wire shape: <c>{ userId, state }</c>.
/// Empty roster list ⇒ no one on the chore (open / single-person). Derived on read by
/// <c>ChoreRosterCalculator</c>; never stored.
/// </summary>
public sealed record RosterMemberDto(int UserId, RosterState State);

/// <summary>
/// Per-room dirtiness rollup. The <see cref="Status"/> bucket is derived from the count of chores in the
/// room whose computed dueness is due-or-overdue (NOT from stored <c>ChoreStatus</c>), bucketed by the
/// named thresholds in <see cref="ChoreRollup"/> (P4): 0 ⇒ clean, 1–2 ⇒ attention, 3+ ⇒ needs-work.
/// The virtual <b>General</b> group (roomless chores) is represented with <see cref="RoomId"/> == null and
/// is NOT backed by a real <c>Room</c> row (D9).
/// </summary>
public sealed record RoomRollupDto(
    int? RoomId,
    string Name,
    string Icon,
    string? PhotoPath,
    int SortOrder,
    int ChoreCount,
    int DueCount,
    RoomRollupStatus Status);

/// <summary>
/// A household member, for rendering minder/assignee tags + avatars on the board (WP-10/12).
/// </summary>
public sealed record MemberDto(
    int UserId,
    string DisplayName,
    string Initials,
    string? PictureUrl);

/// <summary>
/// Dirtiness bucket for a room rollup. Serialized camelCase: <c>clean|attention|needsWork</c>.
/// </summary>
public enum RoomRollupStatus
{
    Clean,
    Attention,
    NeedsWork
}

/// <summary>
/// Named rollup thresholds (P4) — the bucket boundaries for <see cref="RoomRollupStatus"/>, derived from
/// the count of due-or-overdue chores in a room (D9). 0 ⇒ Clean; 1..<see cref="NeedsWorkThreshold"/>-1 ⇒
/// Attention; &gt;= <see cref="NeedsWorkThreshold"/> ⇒ NeedsWork.
/// </summary>
public static class ChoreRollup
{
    /// <summary>The virtual roomless group's display name (D9 — not a real <c>Room</c> row).</summary>
    public const string GeneralGroupName = "General";

    /// <summary>Default icon for the virtual General group.</summary>
    public const string GeneralGroupIcon = "🏠";

    /// <summary>At or above this many due-or-overdue chores, a room is <see cref="RoomRollupStatus.NeedsWork"/>.</summary>
    public const int NeedsWorkThreshold = 3;

    /// <summary>Bucket a due-or-overdue count into a rollup status (0 clean / 1-2 attention / 3+ needs-work).</summary>
    public static RoomRollupStatus BucketFor(int dueCount) => dueCount switch
    {
        <= 0 => RoomRollupStatus.Clean,
        < NeedsWorkThreshold => RoomRollupStatus.Attention,
        _ => RoomRollupStatus.NeedsWork
    };
}
