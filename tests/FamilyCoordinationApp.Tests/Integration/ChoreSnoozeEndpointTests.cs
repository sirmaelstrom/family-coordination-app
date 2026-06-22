using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// HTTP-level verification of <c>PATCH /api/chores/{id}/snooze</c> end to end against REAL Postgres + the real
/// endpoint pipeline — the only layer that proves route registration, <c>DateOnly?</c>/<c>uint version</c> JSON
/// model-binding, the household-tz floor resolution, and the typed-exception → status mapping the service test
/// bypasses. Mirrors <see cref="ChoreConcurrencyTests"/>'s <see cref="ChoresWebAppFactory"/> harness. The app
/// clock is frozen at <see cref="ChoresWebAppFactory.FixedNowUtc"/> (local Sunday 2026-06-07 in
/// America/Chicago), so "today" is 2026-06-07 and <c>{days:3}</c> resolves to 2026-06-10.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreSnoozeEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record BoardChore(int id, uint version, string? snoozedUntil, bool isSnoozed);
    private sealed record Board(List<BoardChore> chores);

    private async Task<BoardChore> ReadPileChoreAsync(HttpClient client)
    {
        var board = await client.GetFromJsonAsync<Board>("/api/chores/board", Json);
        board.Should().NotBeNull();
        var chore = board!.chores.SingleOrDefault(c => c.id == ChoresWebAppFactory.PileChoreAId);
        chore.Should().NotBeNull("the seeded pile chore must be on household A's board");
        return chore!;
    }

    private string SnoozeUrl => $"/api/chores/{ChoresWebAppFactory.PileChoreAId}/snooze";

    private static HttpContent Body(object body) => JsonContent.Create(body, options: Json);

    [Fact]
    public async Task Snooze_WithDays_SetsFloor_ToTodayPlusDays()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var chore = await ReadPileChoreAsync(client);
        chore.snoozedUntil.Should().BeNull("the pile chore starts un-snoozed");

        var resp = await client.PatchAsync(SnoozeUrl, Body(new { days = 3, version = chore.version }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<BoardChore>(Json);
        dto!.snoozedUntil.Should().Be("2026-06-10"); // today (2026-06-07 Chicago) + 3, resolved server-side
        dto.isSnoozed.Should().BeTrue();
        dto.version.Should().NotBe(chore.version, "the snooze write advances the xmin token");
    }

    [Fact]
    public async Task Snooze_WithExplicitUntil_SetsThatDate()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var chore = await ReadPileChoreAsync(client);

        var resp = await client.PatchAsync(SnoozeUrl, Body(new { until = "2026-06-20", version = chore.version }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<BoardChore>(Json);
        dto!.snoozedUntil.Should().Be("2026-06-20");
        dto.isSnoozed.Should().BeTrue();
    }

    [Fact]
    public async Task Snooze_ClearWithNeither_RemovesFloor()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var chore = await ReadPileChoreAsync(client);

        // Snooze first, then clear: the clear body carries NEITHER days nor until — only the version.
        var snoozeResp = await client.PatchAsync(SnoozeUrl, Body(new { days = 3, version = chore.version }));
        snoozeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var snoozed = await snoozeResp.Content.ReadFromJsonAsync<BoardChore>(Json);
        snoozed!.isSnoozed.Should().BeTrue();

        var clearResp = await client.PatchAsync(SnoozeUrl, Body(new { version = snoozed.version }));
        clearResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var cleared = await clearResp.Content.ReadFromJsonAsync<BoardChore>(Json);
        cleared!.snoozedUntil.Should().BeNull();
        cleared.isSnoozed.Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"days\":0}")]                           // days < 1
    [InlineData("{\"days\":3,\"until\":\"2026-06-20\"}")]  // both supplied — ambiguous
    [InlineData("{\"until\":\"2000-01-01\"}")]             // until not in the future
    public async Task Snooze_InvalidInput_Returns400_WithNonEmptyBody(string partialBody)
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var chore = await ReadPileChoreAsync(client);

        // Splice the current version into the partial body so binding succeeds and ResolveSnooze does the reject.
        var body = partialBody.TrimEnd('}') + $",\"version\":{chore.version}}}";
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await client.PatchAsync(SnoozeUrl, content);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await resp.Content.ReadAsStringAsync();
        text.Should().NotBeNullOrWhiteSpace(
            "the 400 body must be non-empty (the UseStatusCodePagesWithReExecute quirk would otherwise swallow it)");
    }

    [Fact]
    public async Task Snooze_StaleVersion_Returns409()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var chore = await ReadPileChoreAsync(client);
        var staleVersion = chore.version;

        var first = await client.PatchAsync(SnoozeUrl, Body(new { days = 3, version = staleVersion }));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Re-using the now-stale version must 409, not silently win.
        var second = await client.PatchAsync(SnoozeUrl, Body(new { days = 5, version = staleVersion }));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
