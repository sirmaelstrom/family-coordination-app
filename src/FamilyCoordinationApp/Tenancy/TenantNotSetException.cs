namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// A tenant query ran with no tenant (fca-household-scope D12). Thrown by the Tenant filter's household getter, which
/// is shared by every tenant entity type, so the message names the tenant STATE, not an entity. It never degrades to
/// an empty or unfiltered result: a missing <c>RequireTenant()</c>, <c>RunAs</c> or bypass fails loudly.
/// </summary>
public sealed class TenantNotSetException : Exception
{
    public TenantNotSetException(string message) : base(message) { }

    /// <summary>The exception for <paramref name="tenant"/>'s current state.</summary>
    public static TenantNotSetException For(ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var state = tenant.IsOutOfRequest
            ? $"Unset outside any HTTP request, with Tenancy:OutOfRequest={tenant.OutOfRequestMode}"
            : $"{tenant.State} inside an HTTP request";
        return new TenantNotSetException(
            $"A tenant-scoped query ran with no tenant (the tenant is {state}). Mark the endpoint with " +
            "RequireTenant(), wrap system work in context.Tenant.RunAs(householdId), or bypass with " +
            "IgnoreQueryFilters([\"Tenant\"]) and a TENANT-SCOPE-OK pragma naming the gate.");
    }
}
