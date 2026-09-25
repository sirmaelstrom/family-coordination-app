using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace FamilyCoordinationApp.Tests.Tenancy;

/// <summary>
/// The central write step on InMemory contexts built with the tenant constructor (fca-household-scope WP-03, V7). Each
/// test seeds and reads back through an options-only (Unfiltered) context over the same store, so what it asserts is
/// what reached the store. The relational cases (the surrogate-key ownership query against real key semantics, the
/// in-request login outcome) are in <c>Integration/TenantWriteIntegrationTests</c>.
/// <para>Negative controls (scratch, in the PR body): without the mismatch branch the <c>HouseholdId = 2</c> add
/// saves; without the Deleted case the stub <c>Remove</c> deletes household 2's room; without the audit stamp the
/// default <c>CreatedAt</c> stays <c>default</c>.</para>
/// </summary>
public class TenantWriteTests
{
    private static readonly DateTime FixedNow = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _store = $"tenant-writes-{Guid.NewGuid():N}";

    private DbContextOptions<ApplicationDbContext> Options() =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(_store).Options;

    /// <summary>A request-scoped tenant: an HttpContext is present, so Unset means Unset-in-request.</summary>
    private static TenantContext InRequest(OutOfRequestMode mode = OutOfRequestMode.Throw) =>
        new(new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Microsoft.Extensions.Options.Options.Create(new TenancyOptions { OutOfRequest = mode }));

    private static TenantContext OutOfRequest(OutOfRequestMode mode) =>
        new(new HttpContextAccessor(), Microsoft.Extensions.Options.Options.Create(new TenancyOptions { OutOfRequest = mode }));

    private static TenantContext Caller(int householdId, int userId = 1)
    {
        var tenant = InRequest();
        tenant.SetCaller(householdId, userId);
        return tenant;
    }

    private ApplicationDbContext Context(ITenantContext tenant, TenancyOptions? tenancy = null) =>
        new(Options(), tenant, tenancy ?? new TenancyOptions()) { Clock = new FixedClock(FixedNow) };

    /// <summary>The options-only constructor: Unfiltered, the bypass state (D12).</summary>
    private ApplicationDbContext Unfiltered() => new(Options());

    private static Room Room(int householdId, int roomId, DateTime createdAt = default) =>
        new() { HouseholdId = householdId, RoomId = roomId, Name = $"room {householdId}/{roomId}", Icon = "r", CreatedAt = createdAt };

    private async Task SeedAsync(params object[] rows)
    {
        await using var db = Unfiltered();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<List<Room>> RoomsInStoreAsync()
    {
        await using var db = Unfiltered();
        return await db.Rooms.AsNoTracking().OrderBy(r => r.HouseholdId).ThenBy(r => r.RoomId).ToListAsync();
    }

    // ── Added: stamp and validate ───────────────────────────────────────────

    // HouseholdId is a foreign key to the store-generated Household.Id, so EF swaps an added 0 for a TEMPORARY value
    // before the write step sees it (InMemory's happens to be 1). Household 2 shows the stamp, not that coincidence.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Caller_an_added_room_with_HouseholdId_0_saves_as_the_callers_household(int household)
    {
        await using (var db = Context(Caller(household)))
        {
            db.Rooms.Add(Room(0, 10));
            await db.SaveChangesAsync();
        }

        (await RoomsInStoreAsync()).Select(r => (r.HouseholdId, r.RoomId)).Should().Equal((household, 10));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Caller_a_recipe_and_its_two_ingredients_added_with_HouseholdId_0_all_save_as_the_callers_household(int household)
    {
        var recipe = new Recipe { RecipeId = 5, Name = "soup" };
        recipe.Ingredients.Add(new RecipeIngredient { RecipeId = 5, IngredientId = 1, Name = "salt" });
        recipe.Ingredients.Add(new RecipeIngredient { RecipeId = 5, IngredientId = 2, Name = "water" });

        await using (var db = Context(Caller(household)))
        {
            db.Recipes.Add(recipe);
            await db.SaveChangesAsync();
            recipe.Ingredients.Should().HaveCount(2, "stamping the composite key keeps the navigation fixed up");
        }

        await using var check = Unfiltered();
        (await check.Recipes.AsNoTracking().Select(r => r.HouseholdId).ToListAsync()).Should().Equal(household);
        (await check.RecipeIngredients.AsNoTracking().Select(i => new { i.HouseholdId, i.RecipeId }).ToListAsync())
            .Should().HaveCount(2).And.OnlyContain(i => i.HouseholdId == household && i.RecipeId == 5);
    }

    [Fact]
    public async Task A_row_added_under_a_household_created_in_the_same_save_is_refused_not_re_pointed()
    {
        var household = new Household { Name = "new" };
        var room = new Room { Household = household, RoomId = 10, Name = "r", Icon = "r" };
        await using (var db = Context(Caller(1)))
        {
            db.Rooms.Add(room);

            // InMemory generates the new household's real id on Add, so this is a plain mismatch here. Npgsql keeps a
            // temporary key until the insert (TenantWriteIntegrationTests covers that message).
            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>();

            // The room's own tracked foreign key, not the household's generated key: it still names the new household.
            db.Entry(room).Property(r => r.HouseholdId).CurrentValue.Should().Be(household.Id,
                "the room was not re-pointed at the tenant's household");
            room.Household.Should().BeSameAs(household);
        }

        (await RoomsInStoreAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Caller_1_an_added_room_of_household_2_throws_and_saves_nothing()
    {
        await using (var db = Context(Caller(1)))
        {
            db.Rooms.Add(Room(2, 10));
            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
                .WithMessage("Added Room {HouseholdId=2, RoomId=10} belongs to household 2, but the tenant is household 1*");
        }

        (await RoomsInStoreAsync()).Should().BeEmpty();
    }

    [Fact]
    public void The_sync_SaveChanges_validates_too()
    {
        using var db = Context(Caller(1));
        db.Rooms.Add(Room(2, 10));

        db.Invoking(d => d.SaveChanges()).Should().ThrowExactly<CrossTenantWriteException>();
    }

    // ── Modified: a HouseholdId never changes ───────────────────────────────

    [Fact]
    public async Task Changing_a_loaded_rooms_HouseholdId_throws()
    {
        await SeedAsync(Room(1, 10, FixedNow));

        await using var db = Context(Caller(1));
        var room = await db.Rooms.SingleAsync();
        room.HouseholdId = 2;

        // HouseholdId is part of Room's primary key, so EF's own key guard refuses first, when the change is detected.
        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>();
        (await RoomsInStoreAsync()).Select(r => r.HouseholdId).Should().Equal(1);
    }

    [Fact]
    public async Task Changing_a_loaded_surrogate_key_rows_HouseholdId_throws_even_inside_AllowCrossTenantWrite()
    {
        await SeedAsync(new User { Id = 7, HouseholdId = 1, Email = "u@a.test", DisplayName = "U", CreatedAt = FixedNow });
        var tenant = Caller(1);

        await using var db = Context(tenant);
        var user = await db.Users.SingleAsync();
        user.HouseholdId = 2;

        using (tenant.AllowCrossTenantWrite("a test"))
        {
            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
                .WithMessage("Modified User {Id=7} moves from household 1 to household 2*");
        }
    }

    [Fact]
    public async Task A_surrogate_key_rows_HouseholdId_marked_modified_but_unchanged_is_refused_with_its_own_message()
    {
        await SeedAsync(new User { Id = 7, HouseholdId = 1, Email = "u@a.test", DisplayName = "U", CreatedAt = FixedNow });

        await using var db = Context(Caller(1));
        var user = await db.Users.SingleAsync();
        db.Entry(user).Property(u => u.HouseholdId).IsModified = true; // what Update() or State = Modified does

        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
            .WithMessage("Modified User {Id=7} has HouseholdId marked modified (household 1, unchanged; the tenant is " +
                         "household 1). It would be written unchanged, and is refused*");
    }

    [Fact]
    public async Task An_added_rows_message_names_its_key_as_it_stands_not_as_first_tracked()
    {
        await using var db = Context(Caller(1));
        var room = Room(2, 10);
        db.Rooms.Add(room);
        room.RoomId = 11; // re-keyed after Add, before the save

        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
            .WithMessage("Added Room {HouseholdId=2, RoomId=11} belongs to household 2*");
    }

    [Fact]
    public void The_tenancy_exceptions_are_not_InvalidOperationExceptions()
    {
        // Endpoint handlers catch InvalidOperationException as "not found" (a 404/409 with no log line); a tenancy
        // refusal must not be caught there (amendment 1; the end-to-end proof is in TenantWriteIntegrationTests).
        typeof(InvalidOperationException).IsAssignableFrom(typeof(CrossTenantWriteException)).Should().BeFalse();
        typeof(InvalidOperationException).IsAssignableFrom(typeof(TenantNotSetException)).Should().BeFalse();
    }

    // ── Deleted ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Caller_1_removing_an_attached_stub_of_household_2s_room_throws_and_the_row_survives()
    {
        await SeedAsync(Room(2, 10, FixedNow));

        await using (var db = Context(Caller(1)))
        {
            db.Rooms.Remove(Room(2, 10));
            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
                .WithMessage("Deleted Room {HouseholdId=2, RoomId=10} belongs to household 2, but the tenant is household 1*");
        }

        (await RoomsInStoreAsync()).Select(r => (r.HouseholdId, r.RoomId)).Should().Equal((2, 10));
    }

    [Fact]
    public async Task Caller_1_removing_its_own_room_saves()
    {
        await SeedAsync(Room(1, 10, FixedNow), Room(2, 10, FixedNow));

        await using (var db = Context(Caller(1)))
        {
            db.Rooms.Remove(await db.Rooms.SingleAsync());
            await db.SaveChangesAsync();
        }

        (await RoomsInStoreAsync()).Select(r => r.HouseholdId).Should().Equal(2);
    }

    // ── Unset ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unset_in_request_a_tenant_add_throws_TenantNotSetException()
    {
        await using (var db = Context(InRequest()))
        {
            db.Rooms.Add(Room(1, 10));
            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<TenantNotSetException>()
                .WithMessage("A tenant-scoped write ran with no tenant (the tenant is Unset inside an HTTP request)*");
        }

        (await RoomsInStoreAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Unset_out_of_request_with_Throw_a_tenant_add_throws()
    {
        await using var db = Context(OutOfRequest(OutOfRequestMode.Throw));
        db.Rooms.Add(Room(1, 10));

        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<TenantNotSetException>()
            .WithMessage("*outside any HTTP request, with Tenancy:OutOfRequest=Throw*");
    }

    [Fact]
    public async Task Unset_a_save_of_only_non_tenant_rows_passes()
    {
        // Setup and admin approve save the Household before their RunAs opens (the WP-02 notes).
        await using var db = Context(InRequest());
        db.Households.Add(new Household { Name = "new" });

        await db.SaveChangesAsync();

        (await db.Households.CountAsync()).Should().Be(1);
    }

    // ── The bypass states are a no-op ───────────────────────────────────────

    // The bypass states behave exactly as with no write step: a mismatch saves, and HouseholdId 0 is left to EF, which
    // refuses it as an unknown key (as it did before WP-03), so it can't save as 0 either.

    [Fact]
    public async Task The_Unfiltered_constructor_saves_a_mismatch_and_leaves_HouseholdId_0_to_EF()
    {
        await using (var db = Unfiltered())
        {
            db.Rooms.Add(Room(2, 10));
            await db.SaveChangesAsync();
        }
        await using (var db = Unfiltered())
        {
            db.Rooms.Add(Room(0, 11));
            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<InvalidOperationException>()
                .WithMessage("The value of 'Room.HouseholdId' is unknown*", "no stamp: EF's own refusal stands");
        }

        (await RoomsInStoreAsync()).Select(r => (r.HouseholdId, r.RoomId)).Should().Equal((2, 10));
    }

    [Fact]
    public async Task Out_of_request_with_OutOfRequest_Unfiltered_is_a_no_op()
    {
        await using (var db = Context(OutOfRequest(OutOfRequestMode.Unfiltered)))
        {
            db.Rooms.Add(Room(2, 10));
            await db.SaveChangesAsync();
        }
        await using (var db = Context(OutOfRequest(OutOfRequestMode.Unfiltered)))
        {
            db.Rooms.Add(Room(0, 11));
            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<InvalidOperationException>()
                .WithMessage("The value of 'Room.HouseholdId' is unknown*", "no stamp: EF's own refusal stands");
        }

        (await RoomsInStoreAsync()).Select(r => (r.HouseholdId, r.RoomId)).Should().Equal((2, 10));
    }

    // ── AllowCrossTenantWrite and the kill switch ───────────────────────────

    [Fact]
    public async Task Inside_AllowCrossTenantWrite_a_modified_invite_of_household_2_saves_under_Caller_1()
    {
        await SeedAsync(new HouseholdInvite { Id = 4, HouseholdId = 2, InviteCode = "ABC234", CreatedByUserId = 9, CreatedAt = FixedNow });
        var tenant = Caller(1);

        await using (var db = Context(tenant))
        {
            // The accept loads the inviting household's row past the Tenant filter, as invite accept does.
            var invite = await db.HouseholdInvites.IgnoreQueryFilters(["Tenant"]).SingleAsync();
            invite.IsUsed = true;
            invite.UsedByHouseholdId = 1;

            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>(
                "without the scope the same write is refused");

            using (tenant.AllowCrossTenantWrite("invite accept"))
            {
                await db.SaveChangesAsync();
            }
        }

        await using var check = Unfiltered();
        (await check.HouseholdInvites.AsNoTracking().SingleAsync()).UsedByHouseholdId.Should().Be(1);
    }

    [Fact]
    public async Task With_EnforceWrites_false_a_mismatch_saves()
    {
        await using (var db = Context(Caller(1), new TenancyOptions { EnforceWrites = false }))
        {
            db.Rooms.Add(Room(2, 10));
            await db.SaveChangesAsync();
        }

        (await RoomsInStoreAsync()).Select(r => r.HouseholdId).Should().Equal(2);
    }

    [Fact]
    public async Task With_EnforceWrites_false_an_Unset_tenant_add_saves()
    {
        await using (var db = Context(InRequest(), new TenancyOptions { EnforceWrites = false }))
        {
            db.Rooms.Add(Room(1, 10));
            await db.SaveChangesAsync();
        }

        (await RoomsInStoreAsync()).Should().ContainSingle();
    }

    // ── The surrogate-key ownership check ───────────────────────────────────

    [Fact]
    public void The_ownership_queries_cover_exactly_the_tenant_types_whose_key_excludes_HouseholdId()
    {
        using var db = Unfiltered();
        var surrogateKeyTenantTypes = db.Model.GetEntityTypes()
            .Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType) && !ApplicationDbContext.KeyIncludesHouseholdId(t))
            .Select(t => t.ClrType);

        ApplicationDbContext.OwnershipQueries.Keys.Should().BeEquivalentTo(surrogateKeyTenantTypes,
            "a Modified/Deleted surrogate-key row with a forged HouseholdId would pass every in-memory check (D9)");
        ApplicationDbContext.OwnershipQueries.Keys.Should().BeEquivalentTo(
            [typeof(User), typeof(HouseholdInvite), typeof(HouseholdCalendarToken)], "today's three (D9)");
    }

    [Fact]
    public async Task A_forged_surrogate_key_update_throws_async_and_sync()
    {
        await SeedAsync(new User { Id = 3, HouseholdId = 2, Email = "bob@b.test", DisplayName = "Bob", CreatedAt = FixedNow });

        await using (var db = Context(Caller(1)))
        {
            var forged = new User { Id = 3, HouseholdId = 1, DisplayName = "x" };
            db.Users.Attach(forged);
            db.Entry(forged).Property(u => u.DisplayName).IsModified = true;

            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowExactlyAsync<CrossTenantWriteException>()
                .WithMessage("Modified User {Id=3} claims household 1, but no such row exists in the tenant's household 1*");
            db.Invoking(d => d.SaveChanges()).Should().ThrowExactly<CrossTenantWriteException>();
        }

        await using var check = Unfiltered();
        (await check.Users.AsNoTracking().SingleAsync()).DisplayName.Should().Be("Bob");
    }

    [Fact]
    public async Task An_owned_surrogate_key_update_saves()
    {
        await SeedAsync(new User { Id = 1, HouseholdId = 1, Email = "a@a.test", DisplayName = "Alice", CreatedAt = FixedNow });

        await using (var db = Context(Caller(1)))
        {
            (await db.Users.SingleAsync()).DisplayName = "Alice A";
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        (await check.Users.AsNoTracking().SingleAsync()).DisplayName.Should().Be("Alice A");
    }

    // ── D16: CreatedAt on insert ────────────────────────────────────────────

    [Fact]
    public async Task An_added_auditable_row_with_a_default_CreatedAt_is_stamped_from_the_clock()
    {
        await using (var db = Context(Caller(1)))
        {
            db.Rooms.Add(Room(1, 10));
            await db.SaveChangesAsync();
        }

        (await RoomsInStoreAsync()).Single().CreatedAt.Should().Be(FixedNow);
    }

    [Fact]
    public async Task An_explicit_CreatedAt_is_kept()
    {
        var explicitValue = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await using (var db = Context(Caller(1)))
        {
            db.Rooms.Add(Room(1, 10, explicitValue));
            await db.SaveChangesAsync();
        }

        (await RoomsInStoreAsync()).Single().CreatedAt.Should().Be(explicitValue);
    }

    [Fact]
    public async Task A_modified_auditable_row_keeps_its_CreatedAt()
    {
        var original = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await SeedAsync(Room(1, 10, original));

        await using (var db = Context(Caller(1)))
        {
            var room = await db.Rooms.SingleAsync();
            room.Name = "renamed";
            room.CreatedAt = default; // even a cleared value is not re-stamped on modify (D16: Added only)
            await db.SaveChangesAsync();
        }

        (await RoomsInStoreAsync()).Single().CreatedAt.Should().Be(default);
    }

    [Fact]
    public async Task A_non_tenant_Household_added_with_a_default_CreatedAt_is_stamped()
    {
        await using (var db = Context(InRequest()))
        {
            db.Households.Add(new Household { Name = "h" });
            await db.SaveChangesAsync();
        }

        await using var check = Unfiltered();
        (await check.Households.AsNoTracking().SingleAsync()).CreatedAt.Should().Be(FixedNow);
    }

    [Fact]
    public async Task Audit_stamping_runs_in_the_bypass_state_and_with_the_kill_switch_off()
    {
        await using (var db = Context(OutOfRequest(OutOfRequestMode.Unfiltered)))
        {
            db.Rooms.Add(Room(1, 10));
            await db.SaveChangesAsync();
        }
        await using (var db = Context(Caller(1), new TenancyOptions { EnforceWrites = false }))
        {
            db.Rooms.Add(Room(1, 11));
            await db.SaveChangesAsync();
        }

        (await RoomsInStoreAsync()).Select(r => r.CreatedAt).Should().Equal(FixedNow, FixedNow);
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
