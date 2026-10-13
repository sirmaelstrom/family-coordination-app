using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Tenancy;
using FamilyCoordinationApp.Tests.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Atomic multi-write commands (quest b942b943). Each command below used to commit in several steps, so a failure
/// partway through left a partial write behind: an orphan household with no categories (setup), a list holding
/// some of its items (shopping generation), an empty meal plan (add meal). Each test injects a fault partway
/// through on real Postgres and asserts nothing of the command survived.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class AtomicMultiWriteCommandTests(PostgresContainerFixture postgres)
{
    /// <summary>Throws from SaveChanges when the change set holds an added entity matching the predicate.</summary>
    private sealed class FaultOnAddedInterceptor(Func<EntityEntry, bool> matches) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var hit = eventData.Context!.ChangeTracker.Entries()
                .Any(e => e.State == EntityState.Added && matches(e));
            if (hit)
            {
                throw new InjectedFaultException();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class InjectedFaultException() : Exception("injected fault");

    /// <summary>A factory over real Postgres whose contexts carry <paramref name="tenant"/> and an optional fault.</summary>
    private sealed class FaultingFactory(string connectionString, ITenantContext tenant, IInterceptor? fault)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(connectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            if (fault is not null)
            {
                builder.AddInterceptors(fault);
            }

            return TestTenancy.Context(builder.Options, tenant);
        }

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private async Task<string> MigratedDatabaseAsync()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        await using var db = await new PostgresDbContextFactory(connectionString).CreateDbContextAsync();
        await db.Database.MigrateAsync();
        return connectionString;
    }

    /// <summary>An in-request tenant with no caller: the first-run <c>/setup</c> page's state.</summary>
    private static TenantContext UnsetInRequestTenant() =>
        new(new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Microsoft.Extensions.Options.Options.Create(new TenancyOptions()));

    // ── SetupService.CreateHouseholdAsync ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(nameof(Category))]  // the first seed: categories
    [InlineData(nameof(Room))]      // the second seed: the chore/room library
    public async Task CreateHousehold_with_a_fault_in_a_seed_leaves_no_household_and_no_user(string faultOn)
    {
        var connectionString = await MigratedDatabaseAsync();
        var fault = new FaultOnAddedInterceptor(e => e.Metadata.ClrType.Name == faultOn);
        var factory = new FaultingFactory(connectionString, UnsetInRequestTenant(), fault);
        var latch = new SetupCompletionLatch();
        var setup = new SetupService(factory, latch, NullLogger<SetupService>.Instance, TestClocks.System);

        var act = () => setup.CreateHouseholdAsync("Orphan Home", "orphan@home.test", "Orphan Owner", "google-orphan");

        await act.Should().ThrowAsync<InjectedFaultException>();

        await using var db = await new PostgresDbContextFactory(connectionString).CreateDbContextAsync();
        (await db.Households.CountAsync()).Should().Be(0, "a seed failure must roll the household back, not orphan it");
        (await db.Users.CountAsync()).Should().Be(0, "the owner row commits with the household or not at all");
        (await db.Categories.CountAsync()).Should().Be(0);
        (await db.Rooms.CountAsync()).Should().Be(0);

        var freshSetup = new SetupService(
            new FaultingFactory(connectionString, UnsetInRequestTenant(), null),
            new SetupCompletionLatch(), NullLogger<SetupService>.Instance, TestClocks.System);
        (await freshSetup.IsSetupCompleteAsync()).Should().BeFalse("setup must not look complete after a failed run");
    }

    [Fact]
    public async Task CreateHousehold_without_a_fault_commits_everything()
    {
        var connectionString = await MigratedDatabaseAsync();
        var factory = new FaultingFactory(connectionString, UnsetInRequestTenant(), null);
        var setup = new SetupService(factory, new SetupCompletionLatch(), NullLogger<SetupService>.Instance, TestClocks.System);

        var (household, user) = await setup.CreateHouseholdAsync("Whole Home", "whole@home.test", "Whole Owner", "google-whole");

        await using var db = await new PostgresDbContextFactory(connectionString).CreateDbContextAsync();
        (await db.Households.CountAsync(h => h.Id == household.Id)).Should().Be(1);
        (await db.Users.SingleAsync(u => u.HouseholdId == household.Id)).Id.Should().Be(user.Id);
        (await db.Categories.CountAsync(c => c.HouseholdId == household.Id)).Should().Be(9);
        (await db.Rooms.CountAsync(r => r.HouseholdId == household.Id)).Should().BePositive();
        (await db.Chores.CountAsync(c => c.HouseholdId == household.Id)).Should().BePositive();
    }

    // ── ShoppingListGenerator.GenerateFromMealPlanAsync ──────────────────────────────────────────

    private static readonly DateOnly WeekStart = new(2026, 9, 7); // a Monday

    /// <summary>Seeds one household, its owner, and a meal plan; returns the household id.</summary>
    private static async Task<int> SeedHouseholdAsync(string connectionString, Action<ApplicationDbContext, int> seedPlan)
    {
        await using var db = await new PostgresDbContextFactory(connectionString).CreateDbContextAsync();
        var household = new Household { Name = "Shopping Home", CreatedAt = DateTime.UtcNow };
        db.Households.Add(household);
        await db.SaveChangesAsync();

        db.Users.Add(new User
        {
            HouseholdId = household.Id, Email = $"owner{household.Id}@shop.test", DisplayName = "Owner",
            IsWhitelisted = true, CreatedAt = DateTime.UtcNow
        });
        db.MealPlans.Add(new MealPlan { HouseholdId = household.Id, MealPlanId = 1, WeekStartDate = WeekStart, CreatedAt = DateTime.UtcNow });
        seedPlan(db, household.Id);
        await db.SaveChangesAsync();
        return household.Id;
    }

    private static Recipe AddRecipe(ApplicationDbContext db, int householdId, int recipeId, string name,
        params (string Name, decimal Qty, string Unit, string Category)[] ingredients)
    {
        var recipe = new Recipe { HouseholdId = householdId, RecipeId = recipeId, Name = name, CreatedAt = DateTime.UtcNow };
        db.Recipes.Add(recipe);
        var id = 0;
        foreach (var (ingName, qty, unit, category) in ingredients)
        {
            db.RecipeIngredients.Add(new RecipeIngredient
            {
                HouseholdId = householdId, RecipeId = recipeId, IngredientId = ++id,
                Name = ingName, Quantity = qty, Unit = unit, Category = category
            });
        }

        return recipe;
    }

    /// <summary>Plans <paramref name="recipeId"/> into <paramref name="count"/> distinct slots of the week.</summary>
    private static void PlanInSlots(ApplicationDbContext db, int householdId, int recipeId, int count, ref int entryId)
    {
        var mealTypes = Enum.GetValues<MealType>();
        for (var i = 0; i < count; i++)
        {
            db.MealPlanEntries.Add(new MealPlanEntry
            {
                HouseholdId = householdId, MealPlanId = 1, EntryId = ++entryId,
                Date = WeekStart.AddDays(i % 7), MealType = mealTypes[i / 7 % mealTypes.Length], RecipeId = recipeId
            });
        }
    }

    private static ShoppingListGenerator Generator(IDbContextFactory<ApplicationDbContext> factory) =>
        new(factory,
            new ShoppingListService(factory, NullLogger<ShoppingListService>.Instance, TestClocks.System, TestKeys.Allocator),
            new UnitConverter(),
            NullLogger<ShoppingListGenerator>.Instance,
            TestClocks.System,
            TestKeys.Allocator);

    [Fact]
    public async Task Generate_with_a_fault_on_one_item_leaves_no_list()
    {
        var connectionString = await MigratedDatabaseAsync();
        var householdId = await SeedHouseholdAsync(connectionString, (db, hid) =>
        {
            AddRecipe(db, hid, 1, "Omelette", ("eggs", 3, "", "Dairy"), ("boom pepper", 1, "tsp", "Spices"));
            var entryId = 0;
            PlanInSlots(db, hid, 1, 1, ref entryId);
        });

        var fault = new FaultOnAddedInterceptor(e => e.Entity is ShoppingListItem { Name: "boom pepper" });
        var factory = new FaultingFactory(connectionString, TestTenancy.Caller(householdId), fault);

        var act = () => Generator(factory).GenerateFromMealPlanAsync(householdId, 1, "Week list");

        await act.Should().ThrowAsync<InjectedFaultException>();

        await using var db = await new PostgresDbContextFactory(connectionString).CreateDbContextAsync();
        (await db.ShoppingLists.CountAsync()).Should().Be(0, "the list commits with its items or not at all");
        (await db.ShoppingListItems.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// P3 (the AG binding): one staple planned in many slots joins one OriginalUnits part per slot, and recipe
    /// names longer than the column join past SourceRecipes. On the base tree the overflowing item's own save
    /// threw (22001) after the list and earlier items had committed: a partial list.
    /// </summary>
    [Fact]
    public async Task Generate_from_a_plan_that_overflows_the_joined_columns_writes_the_whole_list_within_limits()
    {
        var connectionString = await MigratedDatabaseAsync();
        var longName = new string('n', 190);
        var householdId = await SeedHouseholdAsync(connectionString, (db, hid) =>
        {
            // Items before the overflowing ones, so a per-item save would commit them first.
            AddRecipe(db, hid, 1, "Breakfast", ("eggs", 2, "", "Dairy"), ("butter", 1.25m, "tablespoons", "Dairy"));
            var entryId = 0;
            // 20 slots of "1.25 tablespoons" = 20 × 16 + 19 × 3 = 377 chars of OriginalUnits against a 200 limit.
            PlanInSlots(db, hid, 1, 20, ref entryId);
            // Three distinct 190-char recipe names sharing one ingredient: 3 × 190 + 2 × 2 = 574 chars against 500.
            for (var r = 0; r < 3; r++)
            {
                AddRecipe(db, hid, 10 + r, $"{r}{longName}", ("saffron", 1, "pinch", "Spices"));
                db.MealPlanEntries.Add(new MealPlanEntry
                {
                    HouseholdId = hid, MealPlanId = 1, EntryId = ++entryId,
                    Date = WeekStart.AddDays(r), MealType = MealType.Snack, RecipeId = 10 + r
                });
            }
        });

        var factory = new FaultingFactory(connectionString, TestTenancy.Caller(householdId), null);

        var list = await Generator(factory).GenerateFromMealPlanAsync(householdId, 1, "Overflow week");

        await using var db = await new PostgresDbContextFactory(connectionString).CreateDbContextAsync();
        var items = await db.ShoppingListItems.Where(i => i.ShoppingListId == list.ShoppingListId).ToListAsync();
        items.Select(i => i.Name).Should().BeEquivalentTo(["eggs", "butter", "saffron"]);
        items.Should().AllSatisfy(i =>
        {
            (i.OriginalUnits?.Length ?? 0).Should().BeLessThanOrEqualTo(FieldLengths.ShoppingListItem.OriginalUnits);
            (i.SourceRecipes?.Length ?? 0).Should().BeLessThanOrEqualTo(FieldLengths.ShoppingListItem.SourceRecipes);
            (i.RecipeIngredientIds?.Length ?? 0).Should().BeLessThanOrEqualTo(FieldLengths.ShoppingListItem.RecipeIngredientIds);
        });
        items.Single(i => i.Name == "butter").Quantity.Should().Be(25m, "the sum is unaffected by bounding its breakdown");
    }

    // ── MealPlanService.AddMealAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddMeal_into_a_new_week_that_fails_at_the_entry_insert_leaves_no_meal_plan()
    {
        var connectionString = await MigratedDatabaseAsync();
        var householdId = await SeedHouseholdAsync(connectionString, (_, _) => { });
        var factory = new FaultingFactory(connectionString, TestTenancy.Caller(householdId), null);
        var service = new MealPlanService(factory, NullLogger<MealPlanService>.Instance, TestClocks.System, TestKeys.Allocator);
        var newWeek = WeekStart.AddDays(14);

        // Recipe 999 does not exist: the entry's (HouseholdId, RecipeId) FK refuses the insert (23503).
        var act = () => service.AddMealAsync(householdId, newWeek, MealType.Dinner, recipeId: 999, customMealName: null);

        await act.Should().ThrowAsync<DbUpdateException>();

        await using var db = await new PostgresDbContextFactory(connectionString).CreateDbContextAsync();
        (await db.MealPlans.CountAsync(mp => mp.WeekStartDate == newWeek))
            .Should().Be(0, "the week's plan commits with its first entry or not at all");
    }

    [Fact]
    public async Task AddMeal_into_a_new_week_creates_the_plan_and_the_entry_together()
    {
        var connectionString = await MigratedDatabaseAsync();
        var householdId = await SeedHouseholdAsync(connectionString, (_, _) => { });
        var factory = new FaultingFactory(connectionString, TestTenancy.Caller(householdId), null);
        var service = new MealPlanService(factory, NullLogger<MealPlanService>.Instance, TestClocks.System, TestKeys.Allocator);
        var newWeek = WeekStart.AddDays(14);

        var entry = await service.AddMealAsync(householdId, newWeek.AddDays(2), MealType.Lunch, recipeId: null, customMealName: "Leftovers");
        var again = await service.AddMealAsync(householdId, newWeek.AddDays(3), MealType.Lunch, recipeId: null, customMealName: "Eating out");

        await using var db = await new PostgresDbContextFactory(connectionString).CreateDbContextAsync();
        var plan = await db.MealPlans.Include(mp => mp.Entries).SingleAsync(mp => mp.WeekStartDate == newWeek);
        entry.MealPlanId.Should().Be(plan.MealPlanId);
        again.MealPlanId.Should().Be(plan.MealPlanId, "the second add reuses the week's plan");
        plan.Entries.Select(e => e.CustomMealName).Should().BeEquivalentTo(["Leftovers", "Eating out"]);
    }
}
