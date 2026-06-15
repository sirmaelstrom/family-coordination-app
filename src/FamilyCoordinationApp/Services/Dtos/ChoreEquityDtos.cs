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
    IReadOnlyList<MemberShareDto> Members)
{
    /// <summary>
    /// Per-member, ALL-TIME, un-blended planning/coordination tallies (Phase 15 — the system-building
    /// footprint that the physical-only lane misses). Additive <c>init</c>-only property in the record
    /// BODY (NOT a primary-ctor param) so every existing <c>new ChoreEquityDto(...)</c> site compiles
    /// unchanged — the equity endpoint sets it via object initializer / <c>with</c>. Defaults to an empty
    /// list, so it is NEVER null; the island treats <c>planning</c> as always-present. Independent of the
    /// <c>Window</c> param — planning is all-time regardless (D5). NEVER summed/blended with physical points
    /// (MN4).
    /// </summary>
    public IReadOnlyList<MemberPlanningDto> Planning { get; init; } = Array.Empty<MemberPlanningDto>();

    /// <summary>
    /// The REQUESTING user's own physical-capacity tier (<c>Full</c> / <c>Reduced</c> / <c>Minimal</c>;
    /// <c>null</c> ⇒ Full, the pre-migration default). Phase 15 (P4): rides the equity payload so the island's
    /// self-only capacity selector reflects current state without a separate GET. Additive <c>init</c>-only
    /// property in the record BODY (no primary-ctor arity change) — set via object initializer / <c>with</c>.
    /// Serializes camelCase as <c>callerCapacityTier</c>.
    /// </summary>
    public string? CallerCapacityTier { get; init; }
}

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
    double SharePct)
{
    /// <summary>
    /// The member's capacity-WEIGHTED fair share of the physical load (PERCENT 0–100, UNROUNDED — the island
    /// formats; Phase 15 D1/D3). Additive <c>init</c>-only property in the record BODY (no primary-ctor arity
    /// change) so every existing positional <c>new MemberShareDto(7 args)</c> site compiles unchanged — set
    /// via object initializer / <c>with</c>. <c>SharePct</c> stays the RAW actual share (digest-safe). The
    /// island draws this as each member's per-member EXPECTED reference instead of the single flat line.
    /// </summary>
    public double ExpectedSharePct { get; init; }
}

/// <summary>
/// Per-member planning footprint (Phase 15). Plain source-noun count fields (P3) — un-weighted, all-time,
/// NEVER summed into a blended "contribution score" (MN4). Mirrored by the island <c>types.ts</c> in
/// lockstep with <c>equity.json</c> (M5).
/// </summary>
public sealed record MemberPlanningDto(
    int UserId,
    string DisplayName,
    int ChoresSetUp,
    int RecipesAdded,
    int ListItemsCurated,
    int HandOffs);
