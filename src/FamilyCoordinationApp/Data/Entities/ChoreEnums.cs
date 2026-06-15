namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// How a chore recurs. <see cref="OneOff"/> never recurs; <see cref="Fixed"/> recurs on a fixed
/// schedule (interval/weekly/monthly); <see cref="Flexible"/> recurs relative to last completion.
/// </summary>
public enum RecurrenceMode
{
    OneOff,
    Fixed,
    Flexible
}

/// <summary>
/// Named effort tier for a chore (P3 — effort is always a named tier, never a raw number).
/// The underlying integer values are the canonical default <c>EffortPoints</c> for the tier;
/// see <see cref="ChoreEffort.PointsFor"/>.
/// </summary>
public enum EffortTier
{
    Quick = 1,
    Standard = 2,
    BigJob = 3
}

/// <summary>
/// Stored lifecycle state of a chore. Distinct from the computed dueness/freshness result that
/// <c>ChoreStatusCalculator</c> (WP-02) derives on read (council M15).
/// </summary>
public enum ChoreStatus
{
    Active,
    Done,
    Archived
}

/// <summary>
/// How a chore came to be assigned to a user. The three assignment fields move together
/// (council M1 invariant): <c>AssigneeUserId == null ⟺ AssignmentKind == None ⟺ ClaimedAt == null</c>.
/// <see cref="None"/> is the default.
/// </summary>
public enum AssignmentKind
{
    None = 0,
    Assigned,
    Claimed
}

/// <summary>
/// Append-only chore audit event type. For <see cref="AutoReleased"/>, the event's
/// <c>ActorUserId</c> is the lapsed claimer whose claim expired (council M16).
/// </summary>
public enum ChoreEventType
{
    Created,
    Claimed,
    Dropped,
    HandedOff,
    AutoReleased
}

/// <summary>
/// Append-only participation event type for a multi-person chore's named soft roster (rework). The roster
/// and each member's display state are DERIVED by folding these (see <c>ChoreRosterCalculator</c>); "done"
/// is NOT here — it stays in <see cref="ChoreCompletion"/>.
/// <list type="bullet">
///   <item><see cref="Assigned"/> — someone added the subject (a pre-opt-in; declinable, never binding).</item>
///   <item><see cref="Committed"/> — the subject is in (self-opt-in, or confirming an assignment).</item>
///   <item><see cref="Left"/> — the subject was removed from the roster (decline / leave / admin-remove).</item>
/// </list>
/// </summary>
public enum ChoreParticipationType
{
    Assigned,
    Committed,
    Left
}

/// <summary>
/// Custom project flags enum for the days a fixed-weekly chore recurs on. This is a deliberate
/// project type — do NOT wrap <see cref="System.DayOfWeek"/> (which is not a flags enum).
/// </summary>
[Flags]
public enum ChoreDaysOfWeek
{
    None = 0,
    Sunday = 1 << 0,
    Monday = 1 << 1,
    Tuesday = 1 << 2,
    Wednesday = 1 << 3,
    Thursday = 1 << 4,
    Friday = 1 << 5,
    Saturday = 1 << 6
}

/// <summary>
/// Centralized effort-tier → points mapping (P2/P3). Single source of truth for the default
/// <c>EffortPoints</c> assigned to a chore of a given <see cref="EffortTier"/>.
/// </summary>
public static class ChoreEffort
{
    /// <summary>Default effort points for the given tier.</summary>
    public static int PointsFor(EffortTier tier) => (int)tier;
}

/// <summary>
/// Canonical lens-id constants (council M6) — the single source of truth for the board lens
/// identifiers. Referenced by WP-06's <c>PATCH /api/chores/me/default-view</c> allowlist and the
/// island lens switcher (WP-10/12). No ad-hoc casings anywhere else.
/// </summary>
public static class ChoreLens
{
    public const string NeedsAttention = "needs-attention";
    public const string Rooms = "rooms";
    public const string UpForGrabs = "up-for-grabs";
    public const string Mine = "mine";

    /// <summary>
    /// The household equity-distribution lens (v1.1 WP-06, council M16). Canonical id mirrored by the
    /// island <c>types.ts</c>/<c>CHORE_LENSES</c> (WP-09). Including it in <see cref="All"/> lets a user
    /// default their board onto Equity via <c>PATCH /api/chores/me/default-view</c>.
    /// </summary>
    public const string Equity = "equity";

    /// <summary>All valid lens ids, for allowlist validation.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        NeedsAttention,
        Rooms,
        UpForGrabs,
        Mine,
        Equity
    };
}

/// <summary>
/// Canonical physical-capacity tier constants (D2) — the single source of truth for the
/// <c>PATCH /api/chores/me/capacity</c> allowlist (mirrors <see cref="ChoreLens"/>). No ad-hoc casings
/// anywhere else. <c>null</c> and <see cref="Full"/> both mean Full (the pre-migration default); there is
/// no clear-to-null client path.
/// </summary>
public static class CapacityTier
{
    public const string Full = "Full";
    public const string Reduced = "Reduced";
    public const string Minimal = "Minimal";

    /// <summary>All valid capacity tiers, for allowlist validation.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Full,
        Reduced,
        Minimal
    };
}
