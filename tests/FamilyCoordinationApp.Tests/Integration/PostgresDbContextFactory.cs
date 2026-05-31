using FamilyCoordinationApp.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> over the Testcontainers PostgreSQL connection string,
/// configured exactly like production (<c>Program.cs</c>: <c>UseNpgsql</c> + the pending-model-changes warning
/// suppression). Lets the service-level integration tests construct the REAL
/// <see cref="FamilyCoordinationApp.Services.ChoreService"/> against real Postgres without booting the HTTP
/// host — so the operator-mandated <c>xmin</c>→409 concurrency verification (M7/M12) runs even while the HTTP
/// host has a known, separately-reported startup defect.
/// </summary>
public sealed class PostgresDbContextFactory(string connectionString) : IDbContextFactory<ApplicationDbContext>
{
    private DbContextOptions<ApplicationDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

    public ApplicationDbContext CreateDbContext() => new(BuildOptions());

    public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ApplicationDbContext(BuildOptions()));
}
