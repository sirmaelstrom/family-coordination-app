using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// A chore's owner must be a member of the caller's household (quest 65e625ad). The create
/// (<c>POST /api/chores/</c>) and update (<c>PUT /api/chores/{id}</c>) paths stored <c>OwnerUserId</c> unchecked,
/// while the assignee and roster went through <c>EnsureHouseholdMemberAsync</c>; the tenancy write step checks
/// <c>HouseholdId</c> only, not user references. Household A's caller naming household B's user as the owner must
/// get a 400 with a body and change nothing; a same-household owner still succeeds.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreOwnerValidationEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record BoardChore(int id, uint version, int? ownerUserId);
    private sealed record Board(List<BoardChore> chores);
    private sealed record ErrorBody(string message);

    private static HttpContent Body(object body) => JsonContent.Create(body, options: Json);

    private async Task<BoardChore> ReadPileChoreAsync(HttpClient client)
    {
        var board = await client.GetFromJsonAsync<Board>("/api/chores/board", Json);
        return board!.chores.Single(c => c.id == ChoresWebAppFactory.PileChoreAId);
    }

    private async Task<int> CountChoresNamedAsync(string name)
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Chores.CountAsync(c => c.Name == name);
    }

    private static async Task ShouldBeBadRequestWithMessageAsync(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ErrorBody>(Json);
        body!.message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Create_WithAnotherHouseholdsUserAsOwner_Returns400_AndStoresNothing()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var resp = await client.PostAsync("/api/chores/", Body(new
        {
            name = "Foreign owner create",
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "standard",
            ownerUserId = ChoresWebAppFactory.UserBId,
        }));

        await ShouldBeBadRequestWithMessageAsync(resp);
        (await CountChoresNamedAsync("Foreign owner create")).Should().Be(0);
    }

    [Fact]
    public async Task Create_WithSameHouseholdOwner_Succeeds()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var resp = await client.PostAsync("/api/chores/", Body(new
        {
            name = "Member owner create",
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "standard",
            ownerUserId = ChoresWebAppFactory.UserA2Id,
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await resp.Content.ReadFromJsonAsync<BoardChore>(Json);
        created!.ownerUserId.Should().Be(ChoresWebAppFactory.UserA2Id);
    }

    [Fact]
    public async Task Update_WithAnotherHouseholdsUserAsOwner_Returns400_AndLeavesTheOwner()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var chore = await ReadPileChoreAsync(client);

        var resp = await client.PutAsync($"/api/chores/{ChoresWebAppFactory.PileChoreAId}", Body(new
        {
            name = "Pile chore (race target)",
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "standard",
            version = chore.version,
            ownerUserId = ChoresWebAppFactory.UserBId,
        }));

        await ShouldBeBadRequestWithMessageAsync(resp);
        var after = await ReadPileChoreAsync(client);
        after.ownerUserId.Should().Be(chore.ownerUserId);
        after.version.Should().Be(chore.version, "a rejected edit writes nothing");
    }

    [Fact]
    public async Task Update_WithSameHouseholdOwner_Succeeds()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var chore = await ReadPileChoreAsync(client);

        var resp = await client.PutAsync($"/api/chores/{ChoresWebAppFactory.PileChoreAId}", Body(new
        {
            name = "Pile chore (race target)",
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "standard",
            version = chore.version,
            ownerUserId = ChoresWebAppFactory.UserA2Id,
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadPileChoreAsync(client)).ownerUserId.Should().Be(ChoresWebAppFactory.UserA2Id);
    }
}
