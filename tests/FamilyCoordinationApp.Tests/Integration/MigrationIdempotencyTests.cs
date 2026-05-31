using FamilyCoordinationApp.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Prod-safety proof for the migration-designer fix. Three migrations
/// (<c>20260126120000_AddRecipeType</c>, <c>20260126140000_AddUserFavorites</c>,
/// <c>20260131232149_AddShoppingListFavorites</c>) originally shipped WITHOUT a <c>.Designer.cs</c>, so EF never
/// discovered them and skipped them on every database. Their columns/tables were created out-of-band (the
/// duplicate-creation earlier migrations <c>AddMealPlanUpdatedAt</c>/<c>FixPendingChanges</c>, plus a companion
/// <c>.sql</c> for the favorites index). Restoring the Designers makes EF discover them again — which means on
/// an ALREADY-MIGRATED production database (whose <c>__EFMigrationsHistory</c> never recorded these three) EF
/// will see them as pending and run their <c>Up()</c> out of order on the next <c>MigrateAsync</c>, AFTER the
/// columns/tables already exist.
/// <para>This test SIMULATES exactly that production state: migrate a fresh DB fully, then delete the three
/// rows from <c>__EFMigrationsHistory</c> so the columns/tables remain but EF treats those migrations as
/// pending — then run <c>MigrateAsync</c> again and assert it does NOT throw. It only passes because each
/// restored <c>Up()</c> uses idempotent guarded SQL (<c>ADD COLUMN IF NOT EXISTS</c> /
/// <c>CREATE TABLE IF NOT EXISTS</c> / <c>CREATE INDEX IF NOT EXISTS</c>). Without the guards, the re-run would
/// fail with <c>42701 column already exists</c> / <c>42P07 relation already exists</c> — crashing a real prod
/// deploy.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class MigrationIdempotencyTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private static readonly string[] GhostMigrationIds =
    [
        "20260126120000_AddRecipeType",
        "20260126140000_AddUserFavorites",
        "20260131232149_AddShoppingListFavorites"
    ];

    private string _connectionString = default!;

    public async Task InitializeAsync()
        => _connectionString = await postgres.CreateDatabaseConnectionStringAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private DbContextOptions<ApplicationDbContext> Options() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

    [Fact]
    public async Task ReRunningMigrate_OnDbMissingTheGhostHistoryRows_DoesNotThrow_BecauseUpIsIdempotent()
    {
        // 1. Fresh-DB migrate (proves the chain applies cleanly start-to-finish — the fresh-DB property).
        await using (var ctx = new ApplicationDbContext(Options()))
        {
            await ctx.Database.MigrateAsync();
        }

        // 2. Simulate an already-migrated PRODUCTION DB whose history predates the restored Designers: the
        //    ghost migrations were never recorded (the columns/tables exist; the history rows do not).
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var del = conn.CreateCommand();
            del.CommandText =
                "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = ANY(@ids);";
            del.Parameters.AddWithValue("ids", GhostMigrationIds);
            var deleted = await del.ExecuteNonQueryAsync();
            deleted.Should().Be(GhostMigrationIds.Length,
                "the three ghost migrations should have been recorded by the fresh migrate, then removed here");
        }

        // 3. The ghost migrations now look pending; their columns/tables already exist. Re-running MigrateAsync
        //    must NOT throw — the idempotent guarded Up() bodies are safe no-ops on an existing DB. (Without
        //    IF [NOT] EXISTS this throws 42701 / 42P07, which would crash a real prod deploy.)
        await using (var ctx = new ApplicationDbContext(Options()))
        {
            var pendingBefore = (await ctx.Database.GetPendingMigrationsAsync()).ToList();
            pendingBefore.Should().BeEquivalentTo(GhostMigrationIds,
                "the three ghost migrations are exactly the ones treated as pending after their history rows were removed");

            var act = async () => await ctx.Database.MigrateAsync();
            await act.Should().NotThrowAsync(
                "the restored Up() methods use idempotent guarded SQL, so re-applying them on an already-migrated DB is a safe no-op");

            var pendingAfter = (await ctx.Database.GetPendingMigrationsAsync()).ToList();
            pendingAfter.Should().BeEmpty("the re-run records the ghost migrations and leaves nothing pending");
        }

        // 4. Sanity: the data is intact (column + table still present and queryable, not dropped/duplicated).
        await using (var ctx = new ApplicationDbContext(Options()))
        {
            var anyShoppingList = await ctx.ShoppingLists.AnyAsync(); // queries IsFavorite-bearing table
            anyShoppingList.Should().BeFalse("no rows were seeded; the point is the table/column resolve without error");
            var anyFavorite = await ctx.UserFavorites.AnyAsync();
            anyFavorite.Should().BeFalse("the UserFavorites table resolves (was not dropped by the re-run)");
        }
    }
}
