using System.Data.Common;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Quest 0328001a: two concurrent inserts in one household get distinct ids from <c>IKeyAllocator</c>, against real
/// Postgres. The interleaving is forced: an interceptor holds the first insert just before its <c>INSERT</c> runs (its
/// <c>max + 1</c> already read) and commits a rival insert there, so both computed the same id. The first save is then a
/// unique violation on the composite key; the allocator re-runs it, and the re-run reads the rival's row and takes the
/// next id. Negative control: with the retry cut to one attempt the forced tests fail with the 23505 (receipted on the
/// PR).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class KeyAllocatorConcurrencyTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const int HouseholdA = 1;
    private const int HouseholdB = 2;
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private string _connectionString = default!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var ctx = new PostgresDbContextFactory(_connectionString).CreateDbContext();
        await ctx.Database.MigrateAsync();
        ctx.Households.AddRange(
            new Household { Id = HouseholdA, Name = "Key House A", CreatedAt = Now },
            new Household { Id = HouseholdB, Name = "Key House B", CreatedAt = Now });
        ctx.Users.AddRange(
            new User { Id = 1, HouseholdId = HouseholdA, Email = "a@keys.test", DisplayName = "A", Initials = "A", IsWhitelisted = true, CreatedAt = Now },
            new User { Id = 2, HouseholdId = HouseholdB, Email = "b@keys.test", DisplayName = "B", Initials = "B", IsWhitelisted = true, CreatedAt = Now });
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Two_room_creates_that_read_the_same_max_get_distinct_ids()
    {
        var rival = Rooms(Plain(HouseholdA, userId: 1));
        Room? rivalRoom = null;
        var interceptor = RivalBeforeInsert("\"Rooms\"", async () => rivalRoom = await rival.CreateRoomAsync(HouseholdA, "Rival", "🛁"));
        var first = Rooms(new InterceptedFactory(_connectionString, interceptor, TestTenancy.Caller(HouseholdA, 1)));

        var firstRoom = await first.CreateRoomAsync(HouseholdA, "First", "🍳");

        interceptor.Fired.Should().BeTrue("the rival must have committed between the first create's max read and its insert");
        rivalRoom!.RoomId.Should().Be(1);
        firstRoom.RoomId.Should().Be(2, "the first create collided on id 1, was re-run, and took the next id");
        await using var verify = new PostgresDbContextFactory(_connectionString).CreateDbContext();
        (await verify.Rooms.Where(r => r.HouseholdId == HouseholdA).OrderBy(r => r.RoomId).Select(r => new { r.RoomId, r.Name }).ToListAsync())
            .Should().Equal(new { RoomId = 1, Name = "Rival" }, new { RoomId = 2, Name = "First" });
    }

    [Fact]
    public async Task Two_item_adds_to_one_list_that_read_the_same_max_get_distinct_item_ids()
    {
        var setup = Lists(Plain(HouseholdA, userId: 1));
        var list = await setup.CreateShoppingListAsync(HouseholdA, "Groceries");

        var rival = Lists(Plain(HouseholdA, userId: 1));
        var interceptor = RivalBeforeInsert("\"ShoppingListItems\"", () =>
            rival.AddManualItemAsync(new ShoppingListItem { ShoppingListId = list.ShoppingListId, Name = "Rival milk", IsManuallyAdded = true }));
        var first = Lists(new InterceptedFactory(_connectionString, interceptor, TestTenancy.Caller(HouseholdA, 1)));

        var firstItem = await first.AddManualItemAsync(
            new ShoppingListItem { ShoppingListId = list.ShoppingListId, Name = "First eggs", IsManuallyAdded = true });

        interceptor.Fired.Should().BeTrue();
        firstItem.ItemId.Should().Be(2);
        await using var verify = new PostgresDbContextFactory(_connectionString).CreateDbContext();
        (await verify.ShoppingListItems.Where(i => i.HouseholdId == HouseholdA && i.ShoppingListId == list.ShoppingListId)
                .OrderBy(i => i.ItemId).Select(i => i.Name).ToListAsync())
            .Should().Equal("Rival milk", "First eggs");
    }

    [Fact]
    public async Task Two_parallel_room_creates_in_one_household_both_land_with_distinct_ids()
    {
        var a = Rooms(Plain(HouseholdA, userId: 1));
        var b = Rooms(Plain(HouseholdA, userId: 1));

        var rooms = await Task.WhenAll(a.CreateRoomAsync(HouseholdA, "One", "🍳"), b.CreateRoomAsync(HouseholdA, "Two", "🛁"));

        rooms.Select(r => r.RoomId).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task Ids_stay_per_household_and_a_soft_deleted_row_keeps_its_id()
    {
        await Rooms(Plain(HouseholdA, userId: 1)).CreateRoomAsync(HouseholdA, "A1", "🍳");
        await Rooms(Plain(HouseholdA, userId: 1)).CreateRoomAsync(HouseholdA, "A2", "🛁");

        var roomB = await Rooms(Plain(HouseholdB, userId: 2)).CreateRoomAsync(HouseholdB, "B1", "🌳");
        roomB.RoomId.Should().Be(1, "ids are counters within the household, not global");

        var recipes = Recipes(Plain(HouseholdA, userId: 1));
        var deleted = await recipes.CreateRecipeAsync(new Recipe { HouseholdId = HouseholdA, Name = "Gone soup" });
        await recipes.DeleteRecipeAsync(HouseholdA, deleted.RecipeId);
        var next = await recipes.CreateRecipeAsync(new Recipe { HouseholdId = HouseholdA, Name = "New soup" });

        next.RecipeId.Should().Be(deleted.RecipeId + 1, "a soft-deleted recipe's id is never handed out again");
    }

    private PostgresDbContextFactory Plain(int householdId, int userId) =>
        new PostgresDbContextFactory(_connectionString).WithTenant(TestTenancy.Caller(householdId, userId));

    private static RoomService Rooms(IDbContextFactory<ApplicationDbContext> factory) =>
        new(factory, Mock.Of<IImageService>(), NullLogger<RoomService>.Instance, Services.TestClocks.System, Services.TestKeys.Allocator);

    private static ShoppingListService Lists(IDbContextFactory<ApplicationDbContext> factory) =>
        new(factory, NullLogger<ShoppingListService>.Instance, Services.TestClocks.System, Services.TestKeys.Allocator);

    private static RecipeService Recipes(IDbContextFactory<ApplicationDbContext> factory) =>
        new(factory, Mock.Of<IImageService>(), NullLogger<RecipeService>.Instance, Services.TestClocks.System, Services.TestKeys.Allocator);

    /// <summary>
    /// Council round 1 on PR #145 (reproduced by the challenge seat): regenerate A loads a linked list with no generated
    /// items; a rival regenerate commits its generated set before A allocates. A must not add a second set. The rival
    /// fires on A's meal-plan load, which comes after A's list load and before its id allocation. With the snapshot max
    /// A's first id collides with the rival's, the retry re-reads the list and replaces the rival's set; with a
    /// database max (the round-1 head, a0cabe5) A took fresh ids and both sets survived.
    /// </summary>
    [Fact]
    public async Task A_regenerate_racing_a_rival_regenerate_leaves_one_generated_set()
    {
        var plain = Plain(HouseholdA, userId: 1);
        var recipe = await Recipes(plain).CreateRecipeAsync(new Recipe
        {
            HouseholdId = HouseholdA,
            Name = "Pancakes",
            Ingredients =
            [
                new RecipeIngredient { Name = "flour", Quantity = 2, Unit = "cup", SortOrder = 0 },
                new RecipeIngredient { Name = "eggs", Quantity = 3, SortOrder = 1 },
            ],
        });
        var entry = await MealPlans(plain).AddMealAsync(HouseholdA, new DateOnly(2026, 10, 13), MealType.Breakfast, recipe.RecipeId, null, userId: 1);
        var list = await Lists(plain).CreateShoppingListAsync(HouseholdA, "Linked", entry.MealPlanId);

        var rival = Generator(Plain(HouseholdA, userId: 1));
        var interceptor = new RivalBeforeCommand("\"MealPlanEntries\"", () => rival.RegenerateShoppingListAsync(HouseholdA, list.ShoppingListId));
        var first = Generator(new InterceptedFactory(_connectionString, interceptor, TestTenancy.Caller(HouseholdA, 1)));

        await first.RegenerateShoppingListAsync(HouseholdA, list.ShoppingListId);

        interceptor.Fired.Should().BeTrue("the rival must have committed after the first regenerate loaded the list");
        await using var verify = new PostgresDbContextFactory(_connectionString).CreateDbContext();
        (await verify.ShoppingListItems.Where(i => i.HouseholdId == HouseholdA && i.ShoppingListId == list.ShoppingListId && !i.IsManuallyAdded)
                .Select(i => i.Name).ToListAsync())
            .Should().BeEquivalentTo(["flour", "eggs"], "one regenerate's set replaces the other's; two sets is the race");
    }

    private static MealPlanService MealPlans(IDbContextFactory<ApplicationDbContext> factory) =>
        new(factory, NullLogger<MealPlanService>.Instance, Services.TestClocks.System, Services.TestKeys.Allocator);

    private static ShoppingListGenerator Generator(IDbContextFactory<ApplicationDbContext> factory) =>
        new(factory, Lists(factory), new UnitConverter(), NullLogger<ShoppingListGenerator>.Instance,
            Services.TestClocks.System, Services.TestKeys.Allocator);

    private static RivalBeforeCommand RivalBeforeInsert(string table, Func<Task> rival) => new("INSERT INTO " + table, rival);

    /// <summary>Runs <paramref name="rival"/> to completion once, just before the first command containing <paramref name="marker"/>.</summary>
    private sealed class RivalBeforeCommand(string marker, Func<Task> rival) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await MaybeFireAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await MaybeFireAsync(command);
            return result;
        }

        private async Task MaybeFireAsync(DbCommand command)
        {
            if (Fired || !command.CommandText.Contains(marker, StringComparison.Ordinal))
            {
                return;
            }
            Fired = true;
            await rival();
        }
    }

    private sealed class InterceptedFactory(string connectionString, IInterceptor interceptor, ITenantContext tenant)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            TestTenancy.Context(
                new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseNpgsql(connectionString)
                    .AddInterceptors(interceptor)
                    .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                    .Options,
                tenant);

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
