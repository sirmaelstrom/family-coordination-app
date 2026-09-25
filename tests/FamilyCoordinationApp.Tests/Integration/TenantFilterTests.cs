using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The read switch through the real host (fca-household-scope WP-02 step 7b; verification V3/V4/V5). Every query here
/// is written with NO household predicate, from a real-host DI scope whose tenant is set exactly as the middleware
/// sets it, against real Postgres. This is what "isolated by default" means: the filter scopes it.
/// <para>Negative controls (scratch, in the PR body): <c>Tenancy:EnforceFilter=false</c> makes the Alice/Bob cases
/// see both households; the household getter returning 0 turns the Unset throw into an empty result.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class TenantFilterTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const int A = ChoresWebAppFactory.HouseholdAId, B = ChoresWebAppFactory.HouseholdBId;
    private readonly ChoresWebAppFactory _factory = new(postgres);

    public async Task InitializeAsync()
    {
        await _factory.EnsureSeededAsync();

        // The base fixture has one chore per household and no rooms, recipes or categories: add some, per household.
        await using var db = await Unfiltered().CreateDbContextAsync();
        var now = DateTime.UtcNow;
        db.Rooms.AddRange(
            new Room { HouseholdId = A, RoomId = 1, Name = "A kitchen", Icon = "k", SortOrder = 1, CreatedAt = now },
            new Room { HouseholdId = A, RoomId = 2, Name = "A bath", Icon = "b", SortOrder = 2, CreatedAt = now },
            new Room { HouseholdId = B, RoomId = 1, Name = "B kitchen", Icon = "k", SortOrder = 1, CreatedAt = now });
        db.Recipes.AddRange(
            Recipe(A, 1, "A soup", ["A salt", "A water"], now),
            Recipe(B, 1, "B stew", ["B beef"], now));
        db.Categories.AddRange(
            new Category { HouseholdId = A, CategoryId = 1, Name = "A live", SortOrder = 1 },
            new Category { HouseholdId = A, CategoryId = 2, Name = "A gone", SortOrder = 2, IsDeleted = true },
            new Category { HouseholdId = B, CategoryId = 1, Name = "B gone", SortOrder = 1, IsDeleted = true });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private PostgresDbContextFactory Unfiltered() => new(_factory.ConnectionString);

    private static Recipe Recipe(int householdId, int recipeId, string name, string[] ingredients, DateTime now)
    {
        var recipe = new Recipe { HouseholdId = householdId, RecipeId = recipeId, Name = name, CreatedAt = now };
        for (var i = 0; i < ingredients.Length; i++)
        {
            recipe.Ingredients.Add(new RecipeIngredient
            {
                HouseholdId = householdId,
                RecipeId = recipeId,
                IngredientId = i + 1,
                Name = ingredients[i],
                SortOrder = i
            });
        }
        return recipe;
    }

    /// <summary>A real-host request-shaped scope: an HttpContext is present, and the tenant is what the middleware sets.</summary>
    private static (IServiceScope Scope, IDbContextFactory<ApplicationDbContext> Db) InRequest(
        WebApplicationFactory<Program> host, (int HouseholdId, int UserId)? caller)
    {
        var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        if (caller is { } c) scope.ServiceProvider.GetRequiredService<ITenantContext>().SetCaller(c.HouseholdId, c.UserId);
        return (scope, scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>());
    }

    private void LeaveRequest() => _factory.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = null;

    // ── V3: the predicate-free queries ──────────────────────────────────────

    [Theory]
    [InlineData(A, ChoresWebAppFactory.UserAId, 2, "A soup", 2)]
    [InlineData(B, ChoresWebAppFactory.UserBId, 1, "B stew", 1)]
    public async Task A_predicate_free_query_sees_only_the_callers_household(
        int household, int user, int rooms, string recipe, int ingredients)
    {
        var (scope, factory) = InRequest(_factory, (household, user));
        using (scope)
        {
            await using var db = await factory.CreateDbContextAsync();

            (await db.Rooms.Select(r => r.HouseholdId).ToListAsync()).Should().HaveCount(rooms).And.OnlyContain(h => h == household);

            var recipes = await db.Recipes.Include(r => r.Ingredients).ToListAsync();
            recipes.Should().ContainSingle().Which.Name.Should().Be(recipe);
            recipes[0].Ingredients.Should().HaveCount(ingredients).And.OnlyContain(i => i.HouseholdId == household);

            // Name := Name, so the fixture is unchanged; the affected-row count shows which rows were selected.
            (await db.Chores.ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, c => c.Name)))
                .Should().Be(1, "each household has exactly one fixture chore");
        }
        LeaveRequest();
    }

    [Fact]
    public async Task A_predicate_free_ExecuteDelete_removes_only_the_callers_rows()
    {
        await using (var seed = await Unfiltered().CreateDbContextAsync())
        {
            seed.Rooms.AddRange(
                new Room { HouseholdId = A, RoomId = 90, Name = "doomed", Icon = "x", CreatedAt = DateTime.UtcNow },
                new Room { HouseholdId = B, RoomId = 90, Name = "doomed", Icon = "x", CreatedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var (scope, factory) = InRequest(_factory, (A, ChoresWebAppFactory.UserAId));
        using (scope)
        {
            await using var db = await factory.CreateDbContextAsync();
            (await db.Rooms.Where(r => r.Name == "doomed").ExecuteDeleteAsync()).Should().Be(1);
        }
        LeaveRequest();

        await using var check = await Unfiltered().CreateDbContextAsync();
        (await check.Rooms.Where(r => r.Name == "doomed").Select(r => r.HouseholdId).ToListAsync()).Should().Equal(B);
    }

    // ── V4: the Unset tenant fails loudly ───────────────────────────────────

    [Fact]
    public async Task An_Unset_tenant_in_request_throws_and_a_non_tenant_set_does_not()
    {
        var (scope, factory) = InRequest(_factory, caller: null);
        using (scope)
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.Invoking(d => d.Rooms.ToListAsync()).Should().ThrowExactlyAsync<TenantNotSetException>()
                .WithMessage("*Unset inside an HTTP request*");
            (await db.Households.CountAsync()).Should().Be(2, "Households is not a tenant entity");
        }
        LeaveRequest();
    }

    [Fact]
    public async Task Out_of_request_with_the_default_Throw_a_root_provider_query_throws()
    {
        await using var host = _factory.WithWebHostBuilder(b => b.UseSetting("Tenancy:OutOfRequest", "Throw"));
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();

        await db.Invoking(d => d.Rooms.ToListAsync()).Should().ThrowExactlyAsync<TenantNotSetException>()
            .WithMessage("*outside any HTTP request*OutOfRequest=Throw*");
    }

    // ── The bypass cases return everything and never throw ──────────────────

    [Fact]
    public async Task The_options_only_constructor_is_Unfiltered()
    {
        await using var db = await Unfiltered().CreateDbContextAsync();
        (await db.Rooms.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Out_of_request_with_the_test_hosts_Unfiltered_sees_everything()
    {
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        (await db.Rooms.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task The_kill_switch_off_with_the_tenant_Unset_in_request_sees_everything()
    {
        await using var host = _factory.WithWebHostBuilder(b => b.UseSetting("Tenancy:EnforceFilter", "false"));
        var (scope, factory) = InRequest(host, caller: null);
        using (scope)
        {
            await using var db = await factory.CreateDbContextAsync();
            (await db.Rooms.CountAsync()).Should().Be(3, "D14: the switch restores today's behavior, predicates and all");
        }
        LeaveRequest();
    }

    // ── V5: a named soft-delete ignore keeps the Tenant filter ──────────────

    [Fact]
    public async Task Ignoring_SoftDelete_sees_only_the_callers_soft_deleted_rows()
    {
        var (scope, factory) = InRequest(_factory, (A, ChoresWebAppFactory.UserAId));
        using (scope)
        {
            await using var db = await factory.CreateDbContextAsync();
            (await db.Categories.IgnoreQueryFilters(["SoftDelete"]).Where(c => c.IsDeleted).Select(c => c.Name).ToListAsync())
                .Should().Equal("A gone");

            // A former bare site, through the real service: its own predicate plus the filter, one row.
            var categories = await scope.ServiceProvider.GetRequiredService<ICategoryService>()
                .GetCategoriesAsync(A, includeDeleted: true);
            categories.Select(c => c.Name).Should().BeEquivalentTo(["A live", "A gone"]);
        }
        LeaveRequest();
    }
}
