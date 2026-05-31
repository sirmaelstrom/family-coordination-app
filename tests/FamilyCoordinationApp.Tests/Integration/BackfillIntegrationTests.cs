using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// V9 — the dev backfill endpoint (<c>POST /api/chores/seed-starter</c>) on real Postgres + the booted host.
/// A FRESH household (inserted directly so it carries NO seeded rooms/chores) seeds the starter set on the
/// first call (<c>seeded:true</c>); the second call is a no-op (<c>seeded:false</c>) and the room/chore counts
/// are unchanged. The seed is scoped to the caller's household (M1) — the pre-seeded fixture households are
/// untouched.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class BackfillIntegrationTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private const string FreshUserEmail = "carol@household-c.test";

    // Explicit high IDs that won't collide with the seeded households/users (1, 2). The seed inserts explicit
    // IDs, which (with PostgreSQL GENERATED-BY-DEFAULT identity) does NOT advance the identity sequence — so a
    // sequence-generated insert would start at 1 and collide. Mirror the seed: supply an explicit id.
    private const int FreshHouseholdId = 990;
    private const int FreshUserId = 990;

    private sealed record SeedResult(bool seeded);

    /// <summary>Insert a fresh, empty household + whitelisted user directly (NOT via any auto-seeding path),
    /// returning the new HouseholdId. Uses an explicit high id to avoid the identity-sequence collision the
    /// seed's explicit-id inserts would otherwise cause.</summary>
    private async Task<int> InsertFreshHouseholdAsync()
    {
        var dbFactory = new PostgresDbContextFactory(_factory.ConnectionString);
        await using var ctx = await dbFactory.CreateDbContextAsync();

        ctx.Households.Add(new Household
        {
            Id = FreshHouseholdId,
            Name = "Household C (fresh)",
            CreatedAt = DateTime.UtcNow
        });
        ctx.Users.Add(new User
        {
            Id = FreshUserId,
            HouseholdId = FreshHouseholdId,
            Email = FreshUserEmail,
            DisplayName = "Carol C",
            Initials = "CC",
            IsWhitelisted = true,
            CreatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        return FreshHouseholdId;
    }

    private async Task<(int Rooms, int Chores)> CountAsync(int householdId)
    {
        var dbFactory = new PostgresDbContextFactory(_factory.ConnectionString);
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var rooms = await ctx.Rooms.CountAsync(r => r.HouseholdId == householdId);
        var chores = await ctx.Chores.CountAsync(c => c.HouseholdId == householdId);
        return (rooms, chores);
    }

    [Fact]
    public async Task SeedStarter_FreshHousehold_SeedsThenNoOps_ScopedToCaller()
    {
        var freshHouseholdId = await InsertFreshHouseholdAsync();

        // Baseline: the fresh household has no rooms/chores; record A's existing counts for the isolation check.
        var before = await CountAsync(freshHouseholdId);
        before.Rooms.Should().Be(0, "the freshly-inserted household starts empty");
        before.Chores.Should().Be(0);
        var aBefore = await CountAsync(ChoresWebAppFactory.HouseholdAId);

        var client = _factory.CreateClientAs(FreshUserEmail);

        // First seed: creates the starter rooms + chores → seeded:true.
        var firstResp = await client.PostAsync("/api/chores/seed-starter", content: null);
        firstResp.EnsureSuccessStatusCode();
        var first = (await firstResp.Content.ReadFromJsonAsync<SeedResult>(Json))!;
        first.seeded.Should().BeTrue("the first seed of an empty household actually seeds");

        var afterFirst = await CountAsync(freshHouseholdId);
        afterFirst.Rooms.Should().BeGreaterThan(0, "starter rooms were created");
        afterFirst.Chores.Should().BeGreaterThan(0, "starter chores were created");

        // Second seed: idempotent no-op → seeded:false; counts unchanged.
        var secondResp = await client.PostAsync("/api/chores/seed-starter", content: null);
        secondResp.EnsureSuccessStatusCode();
        var second = (await secondResp.Content.ReadFromJsonAsync<SeedResult>(Json))!;
        second.seeded.Should().BeFalse("a household that already has rooms/chores is not re-seeded");

        var afterSecond = await CountAsync(freshHouseholdId);
        afterSecond.Should().Be(afterFirst, "the second seed must not change any counts (idempotent)");

        // Scoped to the caller: household A's counts are untouched by C's backfill (M1).
        var aAfter = await CountAsync(ChoresWebAppFactory.HouseholdAId);
        aAfter.Should().Be(aBefore, "the backfill must only touch the caller's household");
    }
}
