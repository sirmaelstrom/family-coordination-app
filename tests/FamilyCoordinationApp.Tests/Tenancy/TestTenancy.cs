using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

// The test project's root namespace, so every test namespace sees it (and its As extensions) without a using.
namespace FamilyCoordinationApp.Tests;

/// <summary>
/// Tenant-constructed contexts for service-level tests (fca-household-scope WP-05). Since WP-05 the services carry no
/// household predicate and stamp no <c>HouseholdId</c> on most new rows: the Tenant filter and the write step do both.
/// A test that builds its contexts with the options-only constructor runs Unfiltered, so it would see every household
/// and save unset household ids. Build the service's contexts here instead: the tenant starts as <c>Caller</c> for the
/// given household, in request, exactly as <c>CallerTenantMiddleware</c> sets it. A test that calls the service for a
/// second household selects it per call with <c>tenant.RunAs(householdId)</c>. Seed data still goes through an
/// options-only context, which writes every household unchecked.
/// </summary>
internal static class TestTenancy
{
    /// <summary>An in-request tenant whose state is <see cref="TenantState.Caller"/> for <paramref name="householdId"/>.</summary>
    public static TenantContext Caller(int householdId, int userId = 1)
    {
        var tenant = new TenantContext(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Microsoft.Extensions.Options.Options.Create(new TenancyOptions()));
        tenant.SetCaller(householdId, userId);
        return tenant;
    }

    /// <summary>A context over <paramref name="options"/> that reads and writes as <paramref name="tenant"/>.</summary>
    public static ApplicationDbContext Context(DbContextOptions<ApplicationDbContext> options, ITenantContext tenant) =>
        new(options, tenant, new TenancyOptions());

    /// <summary>Run one service call as <paramref name="householdId"/> (a <c>RunAs</c> around the awaited call).</summary>
    public static async Task<T> As<T>(this ITenantContext tenant, int householdId, Func<Task<T>> call)
    {
        using (tenant.RunAs(householdId))
        {
            return await call();
        }
    }

    /// <inheritdoc cref="As{T}"/>
    public static async Task As(this ITenantContext tenant, int householdId, Func<Task> call)
    {
        using (tenant.RunAs(householdId))
        {
            await call();
        }
    }
}
