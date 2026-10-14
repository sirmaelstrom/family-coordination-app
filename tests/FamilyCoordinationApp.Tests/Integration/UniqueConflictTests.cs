using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Quest d9da6ca4: <c>IKeyAllocator.InsertAsync</c> retries only a unique violation it can cure by re-running the insert
/// (an allocated key, or the meal-plan week index). Any other unique violation is a conflict with an existing row: one
/// attempt, then <see cref="ConflictException"/> (409). Before this, a duplicate active category name was retried as an id
/// collision three times and exhausted into <see cref="InvalidOperationException"/> (500). Against real Postgres, so the
/// constraint names are the ones the migrations create.
/// <para>Negative controls (receipted on the PR): on the base tree the create test is red with InvalidOperationException
/// after 2 retry warnings; with a misspelled name in <see cref="IdGenerationHelper.RetryableConstraints"/> the
/// existence test is red naming it.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class UniqueConflictTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const int HouseholdId = 1;
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private string _connectionString = default!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var ctx = new PostgresDbContextFactory(_connectionString).CreateDbContext();
        await ctx.Database.MigrateAsync();
        ctx.Households.Add(new Household { Id = HouseholdId, Name = "Conflict House", CreatedAt = Now });
        ctx.Users.Add(new User { Id = 1, HouseholdId = HouseholdId, Email = "a@conflict.test", DisplayName = "A", Initials = "A", IsWhitelisted = true, CreatedAt = Now });
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_duplicate_active_category_name_is_one_attempt_and_a_conflict()
    {
        var log = new CountingLogger<KeyAllocator>();
        var categories = Categories(log);
        await categories.CreateCategoryAsync(NewCategory("Produce"));

        var act = () => categories.CreateCategoryAsync(NewCategory("Produce"));

        var thrown = await act.Should().ThrowExactlyAsync<ConflictException>();
        thrown.Which.ClientMessage.Should().Be(UniqueViolation.CategoryNameMessage);
        log.Warnings.Should().Be(0, "a name conflict is not an id collision; re-running the insert cannot cure it");
        await using var verify = Plain().CreateDbContext();
        (await verify.Categories.CountAsync(c => c.Name == "Produce")).Should().Be(1);
    }

    [Fact]
    public async Task Renaming_a_category_onto_an_active_name_is_a_conflict()
    {
        var categories = Categories(new CountingLogger<KeyAllocator>());
        await categories.CreateCategoryAsync(NewCategory("Produce"));
        var dairy = await categories.CreateCategoryAsync(NewCategory("Dairy"));

        var rename = new Category { HouseholdId = HouseholdId, CategoryId = dairy.CategoryId, Name = "Produce", Color = "#808080" };
        var act = () => categories.UpdateCategoryAsync(rename);

        (await act.Should().ThrowExactlyAsync<ConflictException>()).Which.ClientMessage.Should().Be(UniqueViolation.CategoryNameMessage);
    }

    [Fact]
    public async Task Restoring_a_category_whose_name_is_active_again_is_a_conflict()
    {
        var categories = Categories(new CountingLogger<KeyAllocator>());
        var first = await categories.CreateCategoryAsync(NewCategory("Produce"));
        await categories.DeleteCategoryAsync(HouseholdId, first.CategoryId);
        await categories.CreateCategoryAsync(NewCategory("Produce")); // the filtered index allows a deleted twin

        var act = () => categories.RestoreCategoryAsync(HouseholdId, first.CategoryId);

        (await act.Should().ThrowExactlyAsync<ConflictException>()).Which.ClientMessage.Should().Be(UniqueViolation.CategoryNameMessage);
    }

    [Fact]
    public async Task Every_retryable_constraint_name_is_a_unique_index_in_the_migrated_schema()
    {
        // A primary key and a unique index both carry a unique index of the constraint's name (pg_index.indisunique);
        // that name is what Postgres reports as the 23505's ConstraintName.
        await using var ctx = Plain().CreateDbContext();
        var uniqueIndexNames = await ctx.Database
            .SqlQueryRaw<string>(
                "SELECT c.relname::text AS \"Value\" FROM pg_class c JOIN pg_index i ON i.indexrelid = c.oid WHERE i.indisunique")
            .ToListAsync();

        IdGenerationHelper.RetryableConstraints.Should().BeSubsetOf(uniqueIndexNames,
            "a misspelled, renamed or non-unique constraint would silently turn its key collisions into 409s");
    }

    private PostgresDbContextFactory Plain() =>
        new PostgresDbContextFactory(_connectionString).WithTenant(TestTenancy.Caller(HouseholdId, 1));

    private CategoryService Categories(ILogger<KeyAllocator> allocatorLog) =>
        new(Plain(), NullLogger<CategoryService>.Instance, Services.TestClocks.System, new KeyAllocator(allocatorLog));

    private static Category NewCategory(string name) =>
        new() { HouseholdId = HouseholdId, Name = name, Color = "#808080" };

    private sealed class CountingLogger<T> : ILogger<T>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }
}
