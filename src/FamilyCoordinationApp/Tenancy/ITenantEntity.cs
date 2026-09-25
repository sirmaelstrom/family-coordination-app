namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// An entity owned by exactly one household through a plain <c>int HouseholdId</c> (fca-household-scope D5).
/// WP-02 applies the named <c>"Tenant"</c> query filter to every entity type implementing this, and WP-03's
/// write step stamps and validates it. <c>Household</c>, <c>HouseholdRequest</c>, <c>Feedback</c>
/// (<c>int?</c>, dual-mode admin) and <c>HouseholdConnection</c> (a pair) deliberately do not implement it.
/// </summary>
public interface ITenantEntity
{
    int HouseholdId { get; set; }
}
