using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The operator-mandated centerpiece (VG1 / M7 / M12): verify that the <c>xmin</c> optimistic-concurrency
/// token on <c>Chore</c> surfaces a real two-writer race as HTTP <b>409</b> against REAL PostgreSQL — the seam
/// the InMemory provider physically cannot exercise (it has no <c>xmin</c> system column and ignores the
/// rowversion).
/// <para><b>The race:</b> read a pile chore's <c>version</c> (xmin), then fire two concurrent
/// <c>POST /claim</c> with the SAME stale version. Exactly one must win (200) and exactly one must lose (409).
/// Never two 200s (= last-write-wins, the bug this harness exists to catch); never two 409s.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreConcurrencyTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record BoardChore(int id, uint version, string assignmentKind, int? assigneeUserId);
    private sealed record Board(List<BoardChore> chores);
    private sealed record VersionBody(uint version);

    private async Task<BoardChore> ReadPileChoreAsync(HttpClient client)
    {
        var board = await client.GetFromJsonAsync<Board>("/api/chores/board", Json);
        board.Should().NotBeNull();
        var chore = board!.chores.SingleOrDefault(c => c.id == ChoresWebAppFactory.PileChoreAId);
        chore.Should().NotBeNull("the seeded pile chore must be on household A's board");
        return chore!;
    }

    [Fact(Skip = ChoresWebAppFactory.HostBlockedSkip)]
    public async Task TwoConcurrentClaims_SameStaleVersion_YieldExactlyOne200AndOne409()
    {
        // Two real household-A members race. Both capture the SAME version of the pile chore, then both POST
        // claim concurrently. (Using two distinct users also proves the loser is rejected by the concurrency
        // token, not by the "already held by you" validation path.)
        var clientA = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var clientA2 = _factory.CreateClientAs(ChoresWebAppFactory.UserA2Email);

        var chore = await ReadPileChoreAsync(clientA);
        chore.assignmentKind.Should().Be("none", "the race target must start unclaimed");
        var staleVersion = chore.version;

        var body = JsonContent.Create(new VersionBody(staleVersion), options: Json);
        var url = $"/api/chores/{ChoresWebAppFactory.PileChoreAId}/claim";

        // Fire both in flight before awaiting either — a genuine concurrent race against real Postgres.
        var task1 = clientA.PostAsync(url, JsonContent.Create(new VersionBody(staleVersion), options: Json));
        var task2 = clientA2.PostAsync(url, JsonContent.Create(new VersionBody(staleVersion), options: Json));
        var responses = await Task.WhenAll(task1, task2);

        var statuses = responses.Select(r => r.StatusCode).ToList();

        statuses.Count(s => s == HttpStatusCode.OK).Should().Be(
            1, "exactly one concurrent claim must win (last-write-wins would be two 200s — the bug)");
        statuses.Count(s => s == HttpStatusCode.Conflict).Should().Be(
            1, "exactly one concurrent claim must lose with a 409 (xmin conflict)");

        // The winner's response carries an advanced version and a real claim; the chore is now held by ONE user.
        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var winnerDto = await winner.Content.ReadFromJsonAsync<BoardChore>(Json);
        winnerDto.Should().NotBeNull();
        winnerDto!.assignmentKind.Should().Be("claimed");
        winnerDto.assigneeUserId.Should().BeOneOf(ChoresWebAppFactory.UserAId, ChoresWebAppFactory.UserA2Id);
        winnerDto.version.Should().NotBe(staleVersion, "the winning write advances the xmin token");

        // The loser did NOT overwrite the winner: the board still shows exactly one claimer.
        var after = await ReadPileChoreAsync(clientA);
        after.assignmentKind.Should().Be("claimed");
        after.assigneeUserId.Should().Be(winnerDto.assigneeUserId);
    }

    [Fact(Skip = ChoresWebAppFactory.HostBlockedSkip)]
    public async Task SecondClaim_WithStaleVersion_Returns409_AfterFirstSucceeds()
    {
        // The sequential proof of the same property: claim once (succeeds, advances xmin), then claim again
        // with the now-stale captured version → 409. (Uses a fresh chore so it does not depend on the
        // concurrent test's ordering.)
        var clientA = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        // Create a brand-new pile chore for an isolated stale-token check.
        var createResp = await clientA.PostAsJsonAsync("/api/chores/", new
        {
            name = "Stale-token target",
            recurrenceMode = "flexible",
            intervalDays = 5,
            effortTier = "quick"
        }, Json);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResp.Content.ReadFromJsonAsync<BoardChore>(Json);
        created.Should().NotBeNull();
        var choreId = created!.id;
        var staleVersion = created.version;

        // First claim succeeds and advances xmin.
        var first = await clientA.PostAsync($"/api/chores/{choreId}/claim",
            JsonContent.Create(new VersionBody(staleVersion), options: Json));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Drop it back to the pile (so the second claim would be legal IF the version were current) — this
        // isolates the failure to the stale xmin token, not the "already held" validation.
        var afterClaim = await first.Content.ReadFromJsonAsync<BoardChore>(Json);
        var dropResp = await clientA.PostAsync($"/api/chores/{choreId}/drop",
            JsonContent.Create(new VersionBody(afterClaim!.version), options: Json));
        dropResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Re-using the ORIGINAL (now stale) version must 409, not 200.
        var second = await clientA.PostAsync($"/api/chores/{choreId}/claim",
            JsonContent.Create(new VersionBody(staleVersion), options: Json));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
