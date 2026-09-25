namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// A <c>SaveChanges</c> would write a tenant row that does not belong to the tenant (fca-household-scope D9, WP-03).
/// Thrown by the central write step before anything reaches the database, for:
/// <list type="bullet">
/// <item>an Added, Modified or Deleted <see cref="ITenantEntity"/> whose <c>HouseholdId</c> is not the tenant's,
/// outside an <see cref="ITenantContext.AllowCrossTenantWrite"/> scope;</item>
/// <item>any change to a row's <c>HouseholdId</c>, and an Added row under a <c>Household</c> created in the same save,
/// both refused even inside that scope;</item>
/// <item>a surrogate-key row that exists, but in another household.</item>
/// </list>
/// The message names the entity type, its key values and both household ids.
/// <para>It derives from <see cref="Exception"/>, NOT <see cref="InvalidOperationException"/>, on purpose: endpoint
/// handlers catch <see cref="InvalidOperationException"/> as "not found" (a 404/409 with no log line), which would
/// hide a refusal from the D13 soak. A refusal must surface as an unhandled exception and be logged.</para>
/// </summary>
public sealed class CrossTenantWriteException : Exception
{
    public CrossTenantWriteException(string message) : base(message) { }

    /// <summary>The row's household is not the tenant's.</summary>
    internal static CrossTenantWriteException Mismatch(string entity, string key, string change, int rowHouseholdId, int tenantHouseholdId) =>
        new($"{change} {entity} {key} belongs to household {rowHouseholdId}, but the tenant is household " +
            $"{tenantHouseholdId}. A cross-household write needs an explicit context.Tenant.AllowCrossTenantWrite(reason) " +
            "scope at an allowlisted call site (D9), or RunAs(householdId) for system work (D11).");

    /// <summary>
    /// The row was added under a <c>Household</c> created in the same save, so its <c>HouseholdId</c> is still EF's
    /// temporary key and can't be the tenant's. Refused even inside an <c>AllowCrossTenantWrite</c> scope: no
    /// sanctioned cross-write creates a household. Save the household first, then add its rows inside
    /// <c>RunAs(household.Id)</c> (the setup and approve paths do exactly this).
    /// </summary>
    internal static CrossTenantWriteException UnderANewHousehold(string entity, int tenantHouseholdId) =>
        new($"Added {entity} belongs to a household created in this same save, but the tenant is household " +
            $"{tenantHouseholdId}. Save the Household first, then add its rows inside context.Tenant.RunAs(household.Id) (D11).");

    /// <summary>
    /// The row's own <c>HouseholdId</c> changed, or is marked modified with an unchanged value (<c>Update()</c>, or
    /// <c>State = Modified</c>, on a surrogate-key row). Never allowed, even inside an <c>AllowCrossTenantWrite</c>
    /// scope.
    /// </summary>
    internal static CrossTenantWriteException Reassigned(string entity, string key, int fromHouseholdId, int toHouseholdId, int tenantHouseholdId) =>
        fromHouseholdId == toHouseholdId
            ? new($"Modified {entity} {key} has HouseholdId marked modified (household {fromHouseholdId}, unchanged; the " +
                  $"tenant is household {tenantHouseholdId}). It would be written unchanged, and is refused: a row's " +
                  "HouseholdId is never part of an update (D9).")
            : new($"Modified {entity} {key} moves from household {fromHouseholdId} to household {toHouseholdId} (the tenant is " +
                  $"household {tenantHouseholdId}). A row's HouseholdId never changes (D9).");

    /// <summary>
    /// A surrogate-key row's key is not in the tenant's household but exists in another one: the tracked
    /// <c>HouseholdId</c> was forged. (A key that exists nowhere is a concurrent delete, and is not this exception.)
    /// </summary>
    internal static CrossTenantWriteException NotOwned(string entity, string key, string change, int trackedHouseholdId, int tenantHouseholdId) =>
        new($"{change} {entity} {key} claims household {trackedHouseholdId}, but no such row exists in the tenant's " +
            $"household {tenantHouseholdId}; it exists in another household. Its key does not include HouseholdId, so " +
            "the write would reach that household's row (D9, the surrogate-key ownership check).");
}
