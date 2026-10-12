using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Real <c>xmin</c> conflicts on <see cref="ShoppingListService.UpdateItemAsync"/>, against real PostgreSQL (quest
/// 8cf3c806, operator answer c: the shopping item PATCH carries a client token, and a stale or missing one is a 409
/// like every other client-token write). The old "checked wins" merge-retry is gone, so a conflict changes nothing.
/// <para>Negative control: on the base tree (merge-retry) the interleaved write is merged and the save succeeds, so
/// <c>A_write_landing_between_load_and_save_is_a_conflict_and_changes_nothing</c> is red there.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ShoppingListItemConflictTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const int HouseholdId = 1;
    private const int ListId = 1;
    private const int ItemId = 1;

    private static readonly DateTime SeededAt = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OtherCheckedAt = new(2026, 6, 1, 12, 5, 0, DateTimeKind.Utc);

    private string _connectionString = default!;
    private PostgresDbContextFactory _dbFactory = default!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        _dbFactory = new PostgresDbContextFactory(_connectionString);

        await using var ctx = _dbFactory.CreateDbContext();
        await ctx.Database.EnsureCreatedAsync();

        ctx.Households.Add(new Household { Id = HouseholdId, Name = "Conflict House", CreatedAt = SeededAt });
        ctx.ShoppingLists.Add(new ShoppingList { HouseholdId = HouseholdId, ShoppingListId = ListId, Name = "Groceries", CreatedAt = SeededAt });
        ctx.ShoppingListItems.Add(new ShoppingListItem
        {
            HouseholdId = HouseholdId,
            ShoppingListId = ListId,
            ItemId = ItemId,
            Name = "Milk",
            Quantity = 1m,
            Category = "Dairy",
            IsChecked = false,
            IsManuallyAdded = true,
            AddedAt = SeededAt
        });
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ShoppingListItem> StoredAsync()
    {
        await using var ctx = _dbFactory.CreateDbContext();
        return await ctx.ShoppingListItems.AsNoTracking().SingleAsync(i =>
            i.HouseholdId == HouseholdId && i.ShoppingListId == ListId && i.ItemId == ItemId);
    }

    private static ShoppingListItem QuantityEdit(decimal quantity) => new()
    {
        HouseholdId = HouseholdId, ShoppingListId = ListId, ItemId = ItemId,
        Name = "Milk", Quantity = quantity, Category = "Dairy", IsChecked = false, CheckedAt = null,
    };

    private ShoppingListService Service(IDbContextFactory<ApplicationDbContext>? factory = null) =>
        new(factory ?? _dbFactory, NullLogger<ShoppingListService>.Instance, Services.TestClocks.System);

    [Fact]
    public async Task A_current_token_saves_and_the_callers_object_carries_the_new_version()
    {
        var before = await StoredAsync();
        var edit = QuantityEdit(3m);

        await Service().UpdateItemAsync(edit, before.Version);

        var after = await StoredAsync();
        after.Quantity.Should().Be(3m);
        after.Version.Should().NotBe(before.Version);
        // The PATCH endpoint answers with the object it passed in (ToItemDto(item)).
        edit.Version.Should().Be(after.Version, "the caller's object must carry the saved xmin, not the pre-save one");
        // Postgres stores microseconds; the in-memory value keeps .NET's 100 ns ticks.
        edit.UpdatedAt.Should().BeCloseTo(after.UpdatedAt!.Value, TimeSpan.FromMicroseconds(1));
    }

    [Theory]
    [InlineData(false)] // a stale token from an earlier read
    [InlineData(true)]  // no token at all (an old client): MissingToken
    public async Task A_stale_or_missing_token_is_a_conflict_and_changes_nothing(bool missing)
    {
        var before = await StoredAsync();
        await Service().UpdateItemAsync(QuantityEdit(2m), before.Version); // advances xmin past `before`
        var token = missing ? ConcurrencySave.MissingToken : before.Version;

        var act = () => Service().UpdateItemAsync(QuantityEdit(9m), token);

        await act.Should().ThrowAsync<ConcurrencyConflictException>();
        (await StoredAsync()).Quantity.Should().Be(2m);
    }

    [Fact]
    public async Task A_write_landing_between_load_and_save_is_a_conflict_and_changes_nothing()
    {
        // The second writer runs between the service's load and its save: it checks the item and commits,
        // advancing xmin, so the service's save is a genuine concurrency conflict rather than a simulated one.
        var token = (await StoredAsync()).Version;
        var otherWriterRan = false;
        var factory = new InterleavingDbContextFactory(_connectionString, async () =>
        {
            await using var other = _dbFactory.CreateDbContext();
            var row = await other.ShoppingListItems.SingleAsync(i =>
                i.HouseholdId == HouseholdId && i.ShoppingListId == ListId && i.ItemId == ItemId);
            row.IsChecked = true;
            row.CheckedAt = OtherCheckedAt;
            await other.SaveChangesAsync();
            otherWriterRan = true;
        });
        // The caller still sees the item unchecked (with the token it read) and edits its quantity.
        var act = () => Service(factory).UpdateItemAsync(QuantityEdit(3m), token);

        await act.Should().ThrowAsync<ConcurrencyConflictException>("no merge, no retry: the client reloads on 409");
        otherWriterRan.Should().BeTrue("the conflicting write must have committed before the service's save");

        var after = await StoredAsync();
        after.IsChecked.Should().BeTrue("the other writer's check stands");
        after.CheckedAt.Should().Be(OtherCheckedAt);
        after.Quantity.Should().Be(1m, "the conflicting edit changed nothing");
    }
}

/// <summary>
/// Hands out <see cref="InterleavingApplicationDbContext"/> instances that run <c>beforeFirstSave</c> once, across
/// every context this factory creates, just before the first <c>SaveChangesAsync</c> reaches the database.
/// </summary>
public sealed class InterleavingDbContextFactory(string connectionString, Func<Task> beforeFirstSave)
    : IDbContextFactory<ApplicationDbContext>
{
    private int _fired;

    private DbContextOptions<ApplicationDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

    private Task FireOnceAsync() =>
        Interlocked.Exchange(ref _fired, 1) == 0 ? beforeFirstSave() : Task.CompletedTask;

    public ApplicationDbContext CreateDbContext() => new InterleavingApplicationDbContext(BuildOptions(), FireOnceAsync);

    public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ApplicationDbContext>(new InterleavingApplicationDbContext(BuildOptions(), FireOnceAsync));
}

/// <summary>An <see cref="ApplicationDbContext"/> that awaits a hook before each save, then saves normally.</summary>
public sealed class InterleavingApplicationDbContext(DbContextOptions<ApplicationDbContext> options, Func<Task> beforeSave)
    : ApplicationDbContext(options)
{
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await beforeSave();
        return await base.SaveChangesAsync(cancellationToken);
    }
}
