namespace FamilyCoordinationApp.Services.Dtos;

/// <summary>
/// The window over which equity is computed (v1.1 — WP-02). <c>Week</c> covers the current
/// Mon–Sun week (local calendar, injected timezone). <c>All</c> has no lower bound.
/// Serialized camelCase via <c>JsonStringEnumConverter(CamelCase)</c> (council M5/M15).
/// </summary>
public enum EquityWindow
{
    Week,
    All
}

/// <summary>
/// Equity distribution payload for the equity lens + digest (v1.1). Frozen here (WP-02 council C1);
/// WP-09 <c>types.ts</c> and WP-06 serialization mirror this exact shape.
/// <para>
/// <c>sharePct</c> / <c>equalSharePct</c> are PERCENT 0–100 (e.g. 41.7). The island renders
/// <c>{sharePct}%</c> directly — no client-side multiply (M5/M6/MN9).
/// </para>
/// <para>
/// <c>fallingBehindCount</c> / <c>upForGrabsCount</c> are computed by the WP-06 endpoint via
/// <c>ChoreAttention</c> predicates (WP-05) over active household chores; they are NOT produced
/// by the calculator itself.
/// </para>
/// </summary>
public sealed record ChoreEquityDto(
    string Window,
    int TotalPoints,
    int TotalCompletions,
    double EqualSharePct,
    int FallingBehindCount,
    int UpForGrabsCount,
    IReadOnlyList<MemberShareDto> Members);

/// <summary>
/// Per-member share in the equity distribution. <c>SharePct</c> is PERCENT 0–100 (council M5).
/// </summary>
public sealed record MemberShareDto(
    int UserId,
    string DisplayName,
    string Initials,
    string? PictureUrl,
    int Points,
    int Completions,
    double SharePct);
