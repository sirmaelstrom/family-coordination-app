namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// A rotatable, read-only capability for one calendar feed of a household. Only the SHA-256 hash of the secret is
/// persisted, so a database read cannot recover a usable feed URL.
/// <para>A token with a <see cref="UserId"/> is that member's own link (quest 8dba87f4): rotating or revoking it
/// touches no other member's link. A token without one is the legacy household-shared link from before per-member
/// links; nothing creates those any more, and the one a household may still have stays readable until a member
/// revokes it.</para>
/// </summary>
public class HouseholdCalendarToken : Tenancy.ITenantEntity, Tenancy.IAuditable, IVersioned
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }

    /// <summary>The member whose link this is, or null for the legacy household-shared link.</summary>
    public int? UserId { get; set; }

    /// <summary>Which feed the token opens.</summary>
    public CalendarFeed Feed { get; set; } = CalendarFeed.MealPlan;

    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public uint Version { get; set; }

    public Household Household { get; set; } = default!;
    public User? User { get; set; }
}

/// <summary>The feed a <see cref="HouseholdCalendarToken"/> opens. Stored as a string.</summary>
public enum CalendarFeed
{
    /// <summary>The household meal plan (every member's link reads the same plan).</summary>
    MealPlan,

    /// <summary>The member's own assigned and claimed chores (quest 3bdbcab7). Always a member link, never shared.</summary>
    Chores,
}
