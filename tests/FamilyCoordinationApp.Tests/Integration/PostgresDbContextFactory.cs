using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> over the Testcontainers PostgreSQL connection string,
/// configured exactly like production (<c>Program.cs</c>: <c>UseNpgsql</c> + the pending-model-changes warning
/// suppression). Lets the service-level integration tests construct the REAL
/// <see cref="FamilyCoordinationApp.Services.ChoreService"/> against real Postgres without booting the HTTP
/// host — so the operator-mandated <c>xmin</c>→409 concurrency verification (M7/M12) runs even while the HTTP
/// host has a known, separately-reported startup defect.
/// <para>With no <paramref name="tenant"/> its contexts are Unfiltered (seeding and verification). A service under
/// test gets one built with a tenant (<see cref="WithTenant"/>): since WP-05 the services state no household
/// themselves, so the tenant's filter and write step do (fca-household-scope WP-05).</para>
/// </summary>
public sealed class PostgresDbContextFactory(string connectionString, ITenantContext? tenant = null)
    : IDbContextFactory<ApplicationDbContext>
{
    /// <summary>The same database, with contexts that read and write as <paramref name="tenantContext"/>.</summary>
    public PostgresDbContextFactory WithTenant(ITenantContext tenantContext) => new(connectionString, tenantContext);

    internal static DbContextOptions<ApplicationDbContext> BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

    public ApplicationDbContext CreateDbContext() =>
        tenant is null ? new(BuildOptions(connectionString)) : TestTenancy.Context(BuildOptions(connectionString), tenant);

    public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
