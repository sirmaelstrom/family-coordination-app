using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Quest 8dba87f4: <c>AddMemberCalendarLinks</c> runs on a production database that already holds household-shared
/// tokens, active and revoked. Migrating a database seeded under the PREVIOUS schema must keep every row as it was
/// (same hash, same revocation), mark it a legacy shared link (<c>UserId</c> NULL) of the meal-plan feed, and leave
/// the active one readable while the revoked ones stay refused. The startup migration tests begin from an empty
/// database, so they cannot show this.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class MemberCalendarLinksMigrationTests(PostgresContainerFixture postgres)
{
    private const string PreviousMigration = "20261012134000_AddMealPlanWeekUniqueIndex";

    [Fact]
    public async Task ExistingSharedTokens_SurviveTheMigration_AsLegacyMealPlanLinks()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var context = new ApplicationDbContext(PostgresDbContextFactory.BuildOptions(connectionString));
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        var revokedAt = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Households" ("Id", "Name", "CreatedAt") VALUES (1, 'Legacy', '2026-09-01T00:00:00Z');
            INSERT INTO "HouseholdCalendarTokens" ("HouseholdId", "TokenHash", "CreatedAt", "RevokedAt") VALUES
                (1, 'AAAA000000000000000000000000000000000000000000000000000000000001', '2026-09-04T00:00:00Z', '2026-09-05T12:00:00Z'),
                (1, 'AAAA000000000000000000000000000000000000000000000000000000000002', '2026-09-05T12:00:00Z', '2026-09-05T12:00:00Z'),
                (1, 'AAAA000000000000000000000000000000000000000000000000000000000003', '2026-09-05T12:00:00Z', NULL);
            """);

        await migrator.MigrateAsync();

        var rows = await context.HouseholdCalendarTokens
            .AsNoTracking()
            .OrderBy(token => token.TokenHash)
            .Select(token => new { token.TokenHash, token.UserId, token.Feed, token.RevokedAt })
            .ToListAsync();
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(row => row.UserId == null && row.Feed == CalendarFeed.MealPlan);
        rows.Select(row => row.RevokedAt).Should().Equal(revokedAt, revokedAt, null);
        // The stored text, not the mapped enum: EF's string-to-enum converter reads an unparseable value (an empty
        // default, say) as default(CalendarFeed), which is MealPlan, so the assertion above could not see it.
        (await context.Database.SqlQueryRaw<string>("""SELECT "Feed" AS "Value" FROM "HouseholdCalendarTokens" """).ToListAsync())
            .Should().Equal("MealPlan", "MealPlan", "MealPlan");
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }
}
