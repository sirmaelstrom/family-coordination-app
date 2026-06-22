using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// HTTP-level verification that the create (<c>POST /api/chores/</c>) and update (<c>PUT /api/chores/{id}</c>)
/// write paths apply the SAME "next-due floor must be in the future" rule as <c>PATCH /snooze</c> — closing the
/// council-found gap where <c>SnoozedUntil</c> ("first due" / "next due") was persisted UNVALIDATED through
/// create/update while quick-snooze rejected it. The app clock is frozen at
/// <see cref="ChoresWebAppFactory.FixedNowUtc"/> (local Sunday 2026-06-07 in America/Chicago), so "today" is
/// 2026-06-07: a floor of that date (or earlier) must 400; a future date must succeed. Mirrors
/// <see cref="ChoreSnoozeEndpointTests"/>'s harness (real Postgres, real endpoint pipeline).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreFloorValidationEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record BoardChore(int id, uint version);
    private sealed record Board(List<BoardChore> chores);

    private static HttpContent Body(object body) => JsonContent.Create(body, options: Json);

    private async Task<BoardChore> ReadPileChoreAsync(HttpClient client)
    {
        var board = await client.GetFromJsonAsync<Board>("/api/chores/board", Json);
        return board!.chores.Single(c => c.id == ChoresWebAppFactory.PileChoreAId);
    }

    [Theory]
    [InlineData("2026-06-07")] // today (the frozen clock) — inert as a floor, must be rejected like PATCH does
    [InlineData("2026-06-01")] // in the past
    public async Task Create_WithNonFutureFirstDue_Returns400(string floor)
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var resp = await client.PostAsync("/api/chores/", Body(new
        {
            name = "Floor test",
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "standard",
            snoozedUntil = floor,
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Create_WithFutureFirstDue_Succeeds()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var resp = await client.PostAsync("/api/chores/", Body(new
        {
            name = "Floor test future",
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "standard",
            snoozedUntil = "2026-06-20",
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Update_WithNonFutureNextDue_Returns400()
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
            snoozedUntil = "2026-06-07", // today → reject
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_WithFutureNextDue_Succeeds()
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
            snoozedUntil = "2026-06-20", // future → ok
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
