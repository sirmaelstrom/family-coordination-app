using FamilyCoordinationApp.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The schema must be live once the host has started, with no request served. Nothing else migrates production:
/// there is no <c>dotnet ef</c> step in the Dockerfile, compose or CI, and the startup block used to be
/// Development-only — which left <c>SetupService</c>'s per-call <c>Database.MigrateAsync</c> as the real
/// production migrator, running on whichever request happened to arrive first after a deploy and re-taking the
/// migrator's <c>ACCESS EXCLUSIVE</c> lock on every request after that.
/// <para>Reuses <see cref="DevAuthTestingWebAppFactory"/> for its plain non-Development host (env "Testing" +
/// a connection string); this test deliberately does NOT call its <c>MigrateAndSeedAsync</c>.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class StartupMigrationTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task HostStartup_AppliesMigrations_BeforeAnyRequestIsServed()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var factory = new DevAuthTestingWebAppFactory(connectionString);

        // Resolving services builds the host. That is the whole exercise — no HttpClient, no request.
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var context = await dbFactory.CreateDbContextAsync();

        (await context.Database.GetAppliedMigrationsAsync()).Should().NotBeEmpty();
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }
}
