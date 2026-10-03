using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// A real <c>xmin</c> conflict on <see cref="ShoppingListService.UpdateItemWithConcurrencyAsync"/> (quest 489954e5),
/// against real PostgreSQL. The service loads the item, a second writer checks it and commits, and only then does
/// the service save — so the save matches zero rows and the <c>catch (DbUpdateConcurrencyException)</c> runs for
/// real. Before the fix that catch read database values through a context the <c>try</c> had already disposed, so a
/// conflict surfaced as an <see cref="ObjectDisposedException"/> (a 500 at the endpoint) and the merge never ran.
/// <para>The intent under test is the catch body's own rule, <c>"Checked wins" - if either user checked it, keep it
/// checked</c>, with last-write-wins for the other fields.</para>
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

    [Fact]
    public async Task ConcurrentCheck_WhileEditingQuantity_CheckedWins_AndTheQuantityEditLands()
    {
        // The second writer runs between the service's load and its first save: it checks the item and commits,
        // advancing xmin, so the service's save is a genuine concurrency conflict rather than a simulated one.
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
        var service = new ShoppingListService(factory, NullLogger<ShoppingListService>.Instance, Services.TestClocks.System);

        // The caller still sees the item unchecked and edits its quantity.
        var edit = new ShoppingListItem
        {
            HouseholdId = HouseholdId,
            ShoppingListId = ListId,
            ItemId = ItemId,
            Name = "Milk",
            Quantity = 3m,
            Category = "Dairy",
            IsChecked = false,
            CheckedAt = null
        };

        var (success, wasConflict, _) = await service.UpdateItemWithConcurrencyAsync(edit);

        otherWriterRan.Should().BeTrue("the conflicting write must have committed before the service's save");
        success.Should().BeTrue("a conflict with a live item merges and retries; it is not a failure");
        wasConflict.Should().BeTrue("the service's first save hit the other writer's xmin");

        await using var ctx = _dbFactory.CreateDbContext();
        var after = await ctx.ShoppingListItems.AsNoTracking().SingleAsync(i =>
            i.HouseholdId == HouseholdId && i.ShoppingListId == ListId && i.ItemId == ItemId);
        after.IsChecked.Should().BeTrue("checked wins: the other user checked it, so it stays checked");
        after.CheckedAt.Should().Be(OtherCheckedAt, "the caller sent no CheckedAt, so the database's is kept");
        after.Quantity.Should().Be(3m, "last write wins for the non-checkbox fields, so the caller's edit lands");

        // The PATCH endpoint answers with the object it passed in (ToItemDto(item)), so the merged row must be
        // copied back onto it — otherwise the client is told the item is unchecked, with a stale Version.
        edit.IsChecked.Should().BeTrue("the caller's object must carry the merged checked state it is answered with");
        edit.CheckedAt.Should().Be(OtherCheckedAt);
        edit.Quantity.Should().Be(3m);
        edit.Version.Should().Be(after.Version, "the caller's object must carry the saved xmin, not the pre-save one");
        // Postgres stores microseconds; the in-memory value keeps .NET's 100 ns ticks.
        edit.UpdatedAt.Should().BeCloseTo(after.UpdatedAt!.Value, TimeSpan.FromMicroseconds(1));
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
