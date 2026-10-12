using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace FamilyCoordinationApp.Tests.Tenancy;

/// <summary>
/// Central audit stamping in the write step (quest e8bf9e1d, operator answer d: stamp the caller everywhere). Each
/// test seeds through an options-only (Unfiltered) context and reads back through another, so what it asserts is
/// what reached the store.
/// <para>Negative controls: on the base tree (01dd1bc, no modify stamp) the soft-delete, sort-order and
/// modified-row tests are red (UpdatedByUserId stays null; UpdatedAt stays as seeded); with the Caller check
/// removed from the step the RunAs tests are red.</para>
/// </summary>
public class AuditStampTests
{
    private static readonly DateTime FixedNow = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int CallerUserId = 7;

    private readonly string _store = $"audit-stamps-{Guid.NewGuid():N}";

    private DbContextOptions<ApplicationDbContext> Options() =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(_store).Options;

    private ApplicationDbContext Context(ITenantContext tenant) =>
        new(Options(), tenant, new TenancyOptions()) { Clock = new FixedClock(FixedNow) };

    private ApplicationDbContext Unfiltered() => new(Options());

    private async Task SeedAsync(params object[] rows)
    {
        await using var db = Unfiltered();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static Recipe Recipe(int createdBy = 3) =>
        new() { HouseholdId = 1, RecipeId = 5, Name = "soup", CreatedByUserId = createdBy, CreatedAt = Earlier };

    // ── Modify: UpdatedAt and the updater ───────────────────────────────────

    [Fact]
    public async Task A_modified_recipe_under_a_caller_gets_UpdatedAt_and_the_callers_UpdatedByUserId()
    {
        await SeedAsync(Recipe());

        await using (var db = Context(TestTenancy.Caller(1, CallerUserId)))
        {
            (await db.Recipes.SingleAsync()).Name = "stew";
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        var row = await check.Recipes.AsNoTracking().SingleAsync();
        row.UpdatedAt.Should().Be(FixedNow);
        row.UpdatedByUserId.Should().Be(CallerUserId);
        row.CreatedByUserId.Should().Be(3, "a modify never re-attributes the creator (planning credit)");
        row.CreatedAt.Should().Be(Earlier);
    }

    [Fact]
    public async Task An_explicit_UpdatedByUserId_and_UpdatedAt_set_by_the_code_win_over_the_stamp()
    {
        await SeedAsync(Recipe());

        await using (var db = Context(TestTenancy.Caller(1, CallerUserId)))
        {
            var recipe = await db.Recipes.SingleAsync();
            recipe.Name = "stew";
            recipe.UpdatedByUserId = 9;
            recipe.UpdatedAt = Earlier;
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        var row = await check.Recipes.AsNoTracking().SingleAsync();
        row.UpdatedByUserId.Should().Be(9);
        row.UpdatedAt.Should().Be(Earlier);
    }

    [Fact]
    public async Task System_work_inside_RunAs_stamps_UpdatedAt_but_leaves_the_updater_unset()
    {
        await SeedAsync(Recipe());
        var tenant = TestTenancy.Caller(1, CallerUserId);

        await using (var db = Context(tenant))
        using (tenant.RunAs(1))
        {
            (await db.Recipes.SingleAsync()).Name = "stew";
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        var row = await check.Recipes.AsNoTracking().SingleAsync();
        row.UpdatedAt.Should().Be(FixedNow);
        row.UpdatedByUserId.Should().BeNull("inside RunAs there is no caller to credit");
    }

    [Fact]
    public async Task An_added_recipe_keeps_UpdatedAt_and_UpdatedByUserId_unset()
    {
        await using (var db = Context(TestTenancy.Caller(1, CallerUserId)))
        {
            db.Recipes.Add(new Recipe { RecipeId = 5, Name = "soup" });
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        var row = await check.Recipes.AsNoTracking().SingleAsync();
        row.UpdatedAt.Should().BeNull();
        row.UpdatedByUserId.Should().BeNull();
        row.CreatedByUserId.Should().Be(CallerUserId);
        row.CreatedAt.Should().Be(FixedNow);
    }

    [Fact]
    public async Task A_modified_meal_plan_entry_gets_UpdatedAt_and_the_callers_UpdatedByUserId()
    {
        await SeedAsync(
            new MealPlan { HouseholdId = 1, MealPlanId = 1, WeekStartDate = new DateOnly(2026, 10, 12), CreatedAt = Earlier },
            new MealPlanEntry { HouseholdId = 1, MealPlanId = 1, EntryId = 1, CustomMealName = "tacos", CreatedByUserId = 3 });

        await using (var db = Context(TestTenancy.Caller(1, CallerUserId)))
        {
            (await db.MealPlanEntries.SingleAsync()).Notes = "extra salsa";
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        var row = await check.MealPlanEntries.AsNoTracking().SingleAsync();
        row.UpdatedAt.Should().Be(FixedNow);
        row.UpdatedByUserId.Should().Be(CallerUserId);
        row.CreatedByUserId.Should().Be(3);
    }

    [Fact]
    public async Task A_modified_shopping_item_gets_UpdatedAt_and_the_callers_UpdatedByUserId()
    {
        await SeedAsync(ShoppingItem(addedBy: 3));

        await using (var db = Context(TestTenancy.Caller(1, CallerUserId)))
        {
            (await db.ShoppingListItems.SingleAsync()).IsChecked = true;
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        var row = await check.ShoppingListItems.AsNoTracking().SingleAsync();
        row.UpdatedAt.Should().Be(FixedNow);
        row.UpdatedByUserId.Should().Be(CallerUserId);
        row.AddedByUserId.Should().Be(3);
    }

    [Fact]
    public async Task A_modified_meal_plan_digest_settings_and_draft_get_UpdatedAt()
    {
        await SeedAsync(
            new MealPlan { HouseholdId = 1, MealPlanId = 1, WeekStartDate = new DateOnly(2026, 10, 12), CreatedAt = Earlier },
            new HouseholdChoreDigestSettings { HouseholdId = 1, CreatedAt = Earlier, UpdatedAt = Earlier },
            new RecipeDraft { HouseholdId = 1, UserId = CallerUserId, RecipeId = 5, DraftJson = "{}", UpdatedAt = Earlier });

        await using (var db = Context(TestTenancy.Caller(1, CallerUserId)))
        {
            (await db.MealPlans.SingleAsync()).WeekStartDate = new DateOnly(2026, 10, 19);
            (await db.ChoreDigestSettings.SingleAsync()).Enabled = true;
            (await db.RecipeDrafts.SingleAsync()).DraftJson = "{\"name\":\"x\"}";
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        (await check.MealPlans.AsNoTracking().SingleAsync()).UpdatedAt.Should().Be(FixedNow);
        (await check.ChoreDigestSettings.AsNoTracking().SingleAsync()).UpdatedAt.Should().Be(FixedNow);
        (await check.RecipeDrafts.AsNoTracking().SingleAsync()).UpdatedAt.Should().Be(FixedNow);
    }

    [Fact]
    public async Task An_unmodified_tracked_row_is_not_stamped()
    {
        await SeedAsync(Recipe());

        await using (var db = Context(TestTenancy.Caller(1, CallerUserId)))
        {
            await db.Recipes.SingleAsync();
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        var row = await check.Recipes.AsNoTracking().SingleAsync();
        row.UpdatedAt.Should().BeNull("a tracked row nobody changed is not written, so it is not stamped");
        row.UpdatedByUserId.Should().BeNull();
    }

    // ── Create: the create-side actor column ────────────────────────────────

    [Fact]
    public async Task An_added_chore_with_EnteredByUserId_0_takes_the_caller_and_an_explicit_one_is_kept()
    {
        await using (var db = Context(TestTenancy.Caller(1, CallerUserId)))
        {
            db.Chores.Add(new Chore { ChoreId = 1, Name = "dishes" });
            db.Chores.Add(new Chore { ChoreId = 2, Name = "trash", EnteredByUserId = 4 });
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        (await check.Chores.AsNoTracking().OrderBy(c => c.ChoreId).Select(c => c.EnteredByUserId).ToListAsync())
            .Should().Equal(CallerUserId, 4);
    }

    [Fact]
    public async Task An_added_invite_with_CreatedByUserId_0_takes_the_caller()
    {
        await using (var db = Context(TestTenancy.Caller(1, CallerUserId)))
        {
            db.HouseholdInvites.Add(new HouseholdInvite { InviteCode = "ABC234", ExpiresAt = FixedNow.AddDays(1) });
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        (await check.HouseholdInvites.AsNoTracking().SingleAsync()).CreatedByUserId.Should().Be(CallerUserId);
    }

    [Fact]
    public async Task An_item_added_inside_RunAs_stays_unattributed()
    {
        var tenant = TestTenancy.Caller(1, CallerUserId);
        await using (var db = Context(tenant))
        using (tenant.RunAs(1))
        {
            db.ShoppingListItems.Add(ShoppingItem(addedBy: null));
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        (await check.ShoppingListItems.AsNoTracking().SingleAsync()).AddedByUserId.Should().BeNull();
    }

    // ── The two modify paths the operator ruled on (answer d) ───────────────

    [Fact]
    public async Task Recipe_soft_delete_records_the_caller_as_the_updater()
    {
        await SeedAsync(Recipe());
        var service = new RecipeService(Factory(TestTenancy.Caller(1, CallerUserId)), Mock.Of<IImageService>(),
            NullLogger<RecipeService>.Instance, TestClocksAt(FixedNow));

        await service.DeleteRecipeAsync(1, 5);

        await using var check = Unfiltered();
        var row = await check.Recipes.IgnoreQueryFilters(["SoftDelete"]).AsNoTracking().SingleAsync();
        row.IsDeleted.Should().BeTrue();
        row.UpdatedAt.Should().Be(FixedNow);
        row.UpdatedByUserId.Should().Be(CallerUserId);
    }

    [Fact]
    public async Task Shopping_sort_order_records_the_caller_as_the_updater()
    {
        await SeedAsync(ShoppingItem(addedBy: 3));
        var service = new ShoppingListService(Factory(TestTenancy.Caller(1, CallerUserId)),
            NullLogger<ShoppingListService>.Instance, TestClocksAt(FixedNow));

        await service.UpdateItemSortOrdersAsync(1, 1, [(1, 4, null)]);

        await using var check = Unfiltered();
        var row = await check.ShoppingListItems.AsNoTracking().SingleAsync();
        row.SortOrder.Should().Be(4);
        row.UpdatedAt.Should().Be(FixedNow);
        row.UpdatedByUserId.Should().Be(CallerUserId);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ShoppingListItem ShoppingItem(int? addedBy) => new()
    {
        HouseholdId = 1, ShoppingListId = 1, ItemId = 1, Name = "milk", AddedByUserId = addedBy, AddedAt = Earlier,
        IsManuallyAdded = addedBy != null,
    };

    private IDbContextFactory<ApplicationDbContext> Factory(ITenantContext tenant)
    {
        var factory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => Context(tenant));
        return factory.Object;
    }

    private static IHouseholdClock TestClocksAt(DateTime utcNow) =>
        new HouseholdClock(new FixedClock(utcNow), TimeZoneInfo.Utc);

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
