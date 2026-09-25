namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// A <c>SaveChanges</c> would write a tenant row that does not belong to the tenant (fca-household-scope D9, WP-03).
/// Thrown by the central write step before anything reaches the database, for an Added, Modified or Deleted
/// <see cref="ITenantEntity"/> whose <c>HouseholdId</c> is not the tenant's (outside an
/// <see cref="ITenantContext.AllowCrossTenantWrite"/> scope), for any change to a row's <c>HouseholdId</c>, and for a
/// surrogate-key row the tenant does not own. The message names the entity type, its key values and both household
/// ids.
/// </summary>
public sealed class CrossTenantWriteException : InvalidOperationException
{
    public CrossTenantWriteException(string message) : base(message) { }

    /// <summary>The row's household is not the tenant's.</summary>
    internal static CrossTenantWriteException Mismatch(string entity, string key, string change, int rowHouseholdId, int tenantHouseholdId) =>
        new($"{change} {entity} {key} belongs to household {rowHouseholdId}, but the tenant is household " +
            $"{tenantHouseholdId}. A cross-household write needs an explicit context.Tenant.AllowCrossTenantWrite(reason) " +
            "scope at an allowlisted call site (D9), or RunAs(householdId) for system work (D11).");

    /// <summary>
    /// The row was added under a <c>Household</c> created in the same save, so its <c>HouseholdId</c> is still EF's
    /// temporary key and can't be the tenant's. Save the household first, then add its rows inside
    /// <c>RunAs(household.Id)</c> (the setup and approve paths do exactly this).
    /// </summary>
    internal static CrossTenantWriteException UnderANewHousehold(string entity, int tenantHouseholdId) =>
        new($"Added {entity} belongs to a household created in this same save, but the tenant is household " +
            $"{tenantHouseholdId}. Save the Household first, then add its rows inside context.Tenant.RunAs(household.Id) (D11).");

    /// <summary>The row's own <c>HouseholdId</c> changed. Never allowed, even inside an <c>AllowCrossTenantWrite</c> scope.</summary>
    internal static CrossTenantWriteException Reassigned(string entity, string key, int fromHouseholdId, int toHouseholdId, int tenantHouseholdId) =>
        new($"Modified {entity} {key} moves from household {fromHouseholdId} to household {toHouseholdId} (the tenant is " +
            $"household {tenantHouseholdId}). A row's HouseholdId never changes (D9).");

    /// <summary>A surrogate-key row's key does not exist under the Tenant filter: the tracked <c>HouseholdId</c> was forged.</summary>
    internal static CrossTenantWriteException NotOwned(string entity, string key, string change, int trackedHouseholdId, int tenantHouseholdId) =>
        new($"{change} {entity} {key} claims household {trackedHouseholdId}, but no such row exists in the tenant's " +
            $"household {tenantHouseholdId}. Its key does not include HouseholdId, so the write would reach another " +
            "household's row (D9, the surrogate-key ownership check).");
}
