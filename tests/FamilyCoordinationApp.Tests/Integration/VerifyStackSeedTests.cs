using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Tenancy;
using FamilyCoordinationApp.Tests.Architecture;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The scratch verify stack's seed (quest 9c8dd002) against the real migrated schema: <c>docker/verify/seed.sql</c>
/// must insert into today's columns, and what it inserts must be enough for the Development startup seed to build the
/// household a verifier walks. A schema change that breaks the SQL fails here, not on the first branch verification.
/// <para>Negative control (receipted on the PR): with the seed's <c>"DisplayName"</c> column misspelled the first test
/// is red with Postgres 42703; with the user insert disabled the user count is red (0); with a <c>container_name</c>
/// added to the app service the compose test is red.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class VerifyStackSeedTests(PostgresContainerFixture postgres)
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(TenantScopeArchitectureTests.AppSourceRoot(), "..", ".."));
    private static string SeedSql => File.ReadAllText(Path.Combine(RepoRoot, "docker", "verify", "seed.sql"));

    [Fact]
    public async Task The_seed_gives_the_startup_seed_a_household_to_build_on_and_is_idempotent()
    {
        var options = await MigratedAsync();

        await RunSeedAsync(options);
        await RunSeedAsync(options);

        var tenancy = Microsoft.Extensions.Options.Options.Create(new TenancyOptions { OutOfRequest = OutOfRequestMode.Throw });
        await SeedData.SeedDevelopmentDataAsync(
            new TenantDbContextFactory(options, new TenantContext(new HttpContextAccessor(), tenancy), tenancy, TimeProvider.System));

        await using var db = new ApplicationDbContext(options);
        var households = await db.Households.IgnoreQueryFilters().ToListAsync();
        households.Should().ContainSingle("the seed is idempotent");
        var householdId = households[0].Id;
        (await db.Users.IgnoreQueryFilters().CountAsync(u => u.Email == "verify@dev.local")).Should().Be(1);
        (await db.Rooms.IgnoreQueryFilters().CountAsync(r => r.HouseholdId == householdId)).Should().BePositive();
        (await db.Chores.IgnoreQueryFilters().CountAsync(c => c.HouseholdId == householdId)).Should().BePositive();
        (await db.Recipes.IgnoreQueryFilters().CountAsync(r => r.HouseholdId == householdId)).Should().BePositive();
    }

    [Fact]
    public void The_compose_file_names_no_container_and_publishes_only_to_loopback()
    {
        var compose = File.ReadAllLines(Path.Combine(RepoRoot, "docker-compose.verify.yml"))
            .Where(line => !line.TrimStart().StartsWith('#'))
            .ToList();

        compose.Should().NotContain(line => line.Contains("container_name"),
            "a fixed name collides with the dev stack's familyapp-* containers");
        var ports = compose.Where(line => line.TrimStart().StartsWith("- \"") && line.Contains(":8080")).ToList();
        ports.Should().ContainSingle().Which.Should().Contain("\"127.0.0.1:", "the scratch stack is for this box only");
        compose.Should().NotContain(line => line.Contains("nginx"));
    }

    private async Task<DbContextOptions<ApplicationDbContext>> MigratedAsync()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseConnectionStringAsync())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var setup = new ApplicationDbContext(options);
        await setup.Database.MigrateAsync();
        return options;
    }

    private static async Task RunSeedAsync(DbContextOptions<ApplicationDbContext> options)
    {
        await using var db = new ApplicationDbContext(options);
        await db.Database.ExecuteSqlRawAsync(SeedSql);
    }
}
