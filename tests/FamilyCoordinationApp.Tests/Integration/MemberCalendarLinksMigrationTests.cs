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
        rows.Select(row => row.TokenHash).Should().Equal(
            "AAAA000000000000000000000000000000000000000000000000000000000001",
            "AAAA000000000000000000000000000000000000000000000000000000000002",
            "AAAA000000000000000000000000000000000000000000000000000000000003");
        rows.Should().OnlyContain(row => row.UserId == null && row.Feed == CalendarFeed.MealPlan);
        rows.Select(row => row.RevokedAt).Should().Equal(revokedAt, revokedAt, null);
        // The stored text, not the mapped enum: EF's string-to-enum converter reads an unparseable value (an empty
        // default, say) as default(CalendarFeed), which is MealPlan, so the assertion above could not see it.
        (await context.Database.SqlQueryRaw<string>("""SELECT "Feed" AS "Value" FROM "HouseholdCalendarTokens" """).ToListAsync())
            .Should().Equal("MealPlan", "MealPlan", "MealPlan");
        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Rolling back past the migration (an operator's hand-run <c>Down</c>) must not fail on the data it allowed:
    /// several active member links in one household, beside the legacy shared one, and a link of another feed.
    /// Down keeps one active row per household (the shared link when one is active, else the newest member link)
    /// and revokes the rest, so the old per-household unique index can be rebuilt.
    /// </summary>
    [Fact]
    public async Task Down_ReducesActiveLinksToOnePerHousehold_PreferringTheSharedLink()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var context = new ApplicationDbContext(PostgresDbContextFactory.BuildOptions(connectionString));
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync();

        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Households" ("Id", "Name", "CreatedAt") VALUES
                (1, 'Shared kept', '2026-09-01T00:00:00Z'),
                (2, 'Members only', '2026-09-01T00:00:00Z');
            INSERT INTO "Users" ("Id", "HouseholdId", "Email", "DisplayName", "IsWhitelisted", "CreatedAt", "Initials") VALUES
                (1, 1, 'a1@x.test', 'A1', true, '2026-09-01T00:00:00Z', 'A'),
                (2, 1, 'a2@x.test', 'A2', true, '2026-09-01T00:00:00Z', 'A'),
                (3, 2, 'b1@x.test', 'B1', true, '2026-09-01T00:00:00Z', 'B'),
                (4, 2, 'b2@x.test', 'B2', true, '2026-09-01T00:00:00Z', 'B');
            INSERT INTO "HouseholdCalendarTokens" ("HouseholdId", "UserId", "Feed", "TokenHash", "CreatedAt", "RevokedAt") VALUES
                (1, NULL, 'MealPlan', 'H1-SHARED', '2026-09-04T00:00:00Z', NULL),
                (1, 1, 'MealPlan', 'H1-A1', '2026-10-01T00:00:00Z', NULL),
                (1, 2, 'MealPlan', 'H1-A2', '2026-10-02T00:00:00Z', NULL),
                (2, 3, 'MealPlan', 'H2-B1', '2026-10-01T00:00:00Z', NULL),
                (2, 4, 'MealPlan', 'H2-B2', '2026-10-03T00:00:00Z', NULL),
                (2, 4, 'Chores', 'H2-B2-CHORES', '2026-10-04T00:00:00Z', NULL);
            """);

        await migrator.MigrateAsync(PreviousMigration);

        var active = await context.Database
            .SqlQueryRaw<string>("""SELECT "TokenHash" AS "Value" FROM "HouseholdCalendarTokens" WHERE "RevokedAt" IS NULL ORDER BY "HouseholdId" """)
            .ToListAsync();
        active.Should().Equal(["H1-SHARED", "H2-B2"],
            "one active link per household: the shared one where it exists, else the newest meal-plan member link; a chores link never becomes a meal-plan link");
    }
}
