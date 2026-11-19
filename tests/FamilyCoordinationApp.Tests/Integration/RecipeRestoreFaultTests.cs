using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tenancy;
using FamilyCoordinationApp.Tests.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Fault injection for recipe restore, on real Postgres. Restore is one write: the <c>IsDeleted</c> flag of one
/// row. Each test makes a step fail and asserts what the database holds afterwards:
/// <list type="bullet">
/// <item>the save throws: the recipe is still deleted, and its row was not written (same <c>xmin</c>);</item>
/// <item>another write lands between restore's read and its save: restore loses
/// (<see cref="DbUpdateConcurrencyException"/>, a 409 on <c>/api</c>), the other write stands, the recipe is still
/// deleted, and a retry restores it;</item>
/// <item>the answer is lost after a successful save: the retry writes nothing and succeeds.</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class RecipeRestoreFaultTests(PostgresContainerFixture postgres)
{
    private sealed class InjectedFaultException() : Exception("injected fault");

    /// <summary>Runs <paramref name="beforeSave"/> as SaveChanges starts, once: it may throw, or write a rival change.</summary>
    private sealed class BeforeSaveInterceptor(Func<Task> beforeSave) : SaveChangesInterceptor
    {
        private bool _ran;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_ran)
            {
                _ran = true;
                await beforeSave();
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>A factory over real Postgres whose contexts carry <paramref name="tenant"/> and an optional interceptor.</summary>
    private sealed class InterceptedFactory(string connectionString, ITenantContext tenant, IInterceptor? interceptor)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(connectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            if (interceptor is not null)
            {
                builder.AddInterceptors(interceptor);
            }

            return TestTenancy.Context(builder.Options, tenant);
        }

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private const int RecipeId = 1;
    private static readonly DateTime DeletedAt = new(2026, 6, 20, 14, 30, 0, DateTimeKind.Utc);

    private sealed record Seeded(string ConnectionString, int HouseholdId, int UserId);

    /// <summary>A migrated database holding one household, its owner, and one DELETED recipe.</summary>
    private async Task<Seeded> SeedDeletedRecipeAsync()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var db = await new PostgresDbContextFactory(connectionString).CreateDbContextAsync();
        await db.Database.MigrateAsync();

        var household = new Household { Name = "Restore Home", CreatedAt = DateTime.UtcNow };
        db.Households.Add(household);
        await db.SaveChangesAsync();

        var owner = new User
        {
            HouseholdId = household.Id, Email = "owner@restore.test", DisplayName = "Owner",
            IsWhitelisted = true, CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(owner);
        db.Recipes.Add(new Recipe
        {
            HouseholdId = household.Id, RecipeId = RecipeId, Name = "Binned Soup",
            IsDeleted = true, CreatedAt = DeletedAt.AddDays(-30), UpdatedAt = DeletedAt
        });
        await db.SaveChangesAsync();

        return new Seeded(connectionString, household.Id, owner.Id);
    }

    private static RecipeService ServiceOver(Seeded seeded, IInterceptor? interceptor) => new(
        new InterceptedFactory(seeded.ConnectionString, TestTenancy.Caller(seeded.HouseholdId, seeded.UserId), interceptor),
        Mock.Of<IImageService>(), NullLogger<RecipeService>.Instance, TestClocks.System, TestKeys.Allocator);

    /// <summary>The recipe row as stored, read with no tenant and no soft-delete filter.</summary>
    private static async Task<Recipe> StoredAsync(Seeded seeded)
    {
        await using var db = await new PostgresDbContextFactory(seeded.ConnectionString).CreateDbContextAsync();
        return await db.Recipes.IgnoreQueryFilters(["SoftDelete"]).AsNoTracking()
            .SingleAsync(r => r.HouseholdId == seeded.HouseholdId && r.RecipeId == RecipeId);
    }

    [Fact]
    public async Task Restore_whose_save_fails_leaves_the_recipe_deleted_and_its_row_unwritten()
    {
        var seeded = await SeedDeletedRecipeAsync();
        var before = await StoredAsync(seeded);
        var service = ServiceOver(seeded, new BeforeSaveInterceptor(() => throw new InjectedFaultException()));

        var act = () => service.RestoreRecipeAsync(RecipeId);

        await act.Should().ThrowAsync<InjectedFaultException>();
        var after = await StoredAsync(seeded);
        after.IsDeleted.Should().BeTrue("a failed restore must not half-restore the recipe");
        after.Version.Should().Be(before.Version, "the row was not written at all");
        after.UpdatedAt.Should().Be(DeletedAt, "so the Deleted Recipes list still shows when it was deleted");
        (await ServiceOver(seeded, null).GetDeletedRecipesAsync()).Should().ContainSingle(r => r.RecipeId == RecipeId);
    }

    [Fact]
    public async Task Restore_that_loses_a_race_throws_a_concurrency_conflict_keeps_the_other_write_and_a_retry_restores()
    {
        var seeded = await SeedDeletedRecipeAsync();
        var rival = new BeforeSaveInterceptor(async () =>
        {
            // Another writer changes the row after restore read it and before restore saves it.
            await using var db = await new PostgresDbContextFactory(seeded.ConnectionString).CreateDbContextAsync();
            var row = await db.Recipes.IgnoreQueryFilters(["SoftDelete"])
                .SingleAsync(r => r.HouseholdId == seeded.HouseholdId && r.RecipeId == RecipeId);
            row.Name = "Binned Soup (renamed by a rival)";
            await db.SaveChangesAsync();
        });

        var act = () => ServiceOver(seeded, rival).RestoreRecipeAsync(RecipeId);

        // The /api exception branch answers this type with 409 (ApiExceptionResponse).
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        var afterConflict = await StoredAsync(seeded);
        afterConflict.IsDeleted.Should().BeTrue("the stale restore must not land on top of the rival's write");
        afterConflict.Name.Should().Be("Binned Soup (renamed by a rival)");

        await ServiceOver(seeded, null).RestoreRecipeAsync(RecipeId);

        var afterRetry = await StoredAsync(seeded);
        afterRetry.IsDeleted.Should().BeFalse("the retry reads the current row and restores it");
        afterRetry.Name.Should().Be("Binned Soup (renamed by a rival)");
    }

    [Fact]
    public async Task Restore_retried_after_it_already_succeeded_writes_nothing()
    {
        var seeded = await SeedDeletedRecipeAsync();
        var service = ServiceOver(seeded, null);

        await service.RestoreRecipeAsync(RecipeId);
        var restored = await StoredAsync(seeded);
        restored.IsDeleted.Should().BeFalse();
        restored.UpdatedByUserId.Should().Be(seeded.UserId, "the write step stamps who restored it");
        restored.UpdatedAt.Should().BeAfter(DeletedAt);

        // The lost-answer case: the client never saw the 204 and asks again.
        await service.RestoreRecipeAsync(RecipeId);

        var again = await StoredAsync(seeded);
        again.Version.Should().Be(restored.Version, "an already-active recipe is not written again");
        (await service.GetDeletedRecipesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_of_an_id_that_does_not_exist_throws_NotFound_and_writes_nothing()
    {
        var seeded = await SeedDeletedRecipeAsync();
        var before = await StoredAsync(seeded);

        var act = () => ServiceOver(seeded, null).RestoreRecipeAsync(RecipeId + 1);

        await act.Should().ThrowAsync<NotFoundException>();
        (await StoredAsync(seeded)).Version.Should().Be(before.Version);
    }
}
