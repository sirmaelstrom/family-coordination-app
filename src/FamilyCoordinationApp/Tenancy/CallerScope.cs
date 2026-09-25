namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// The resolved caller, as a minimal-API handler parameter (fca-household-scope D4). Bound from the request's
/// <see cref="ITenantContext"/>, which <see cref="CallerTenantMiddleware"/> has already set on a marked route. No
/// handler takes it yet: WP-04 swaps the per-handler resolve-or-401 idiom for it.
/// </summary>
public sealed record CallerScope(int HouseholdId, int UserId)
{
    /// <summary>
    /// Minimal-API binding. Throws when the tenant is not in the Caller state: the endpoint is missing
    /// <c>RequireTenant()</c>, and that must be a loud 500, never an unscoped run.
    /// </summary>
    public static ValueTask<CallerScope?> BindAsync(HttpContext context)
    {
        var tenant = context.RequestServices.GetRequiredService<ITenantContext>();
        if (tenant.State != TenantState.Caller || tenant.HouseholdId is not { } householdId || tenant.UserId is not { } userId)
        {
            throw new InvalidOperationException(
                $"CallerScope requires the Caller tenant state, but the tenant is {tenant.State}. " +
                "Is the endpoint's route group missing RequireTenant()?");
        }
        return ValueTask.FromResult<CallerScope?>(new CallerScope(householdId, userId));
    }
}
