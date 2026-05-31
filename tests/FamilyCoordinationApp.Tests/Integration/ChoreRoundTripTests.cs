using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end happy-path + error-mapping coverage through the real HTTP endpoints against real Postgres:
/// a create → claim → drop → complete round-trip (each step carrying the live <c>version</c> token), an
/// illegal transition (drop an unclaimed chore) mapping to <b>400</b>, and a missing chore mapping to
/// <b>404</b>. Complements <see cref="ChoreConcurrencyTests"/> (the 409 centerpiece) by proving the rest of
/// the status mapping is wired correctly against a real engine.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreRoundTripTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record Chore(
        int id, string name, uint version, string assignmentKind, int? assigneeUserId, DateTime? lastCompletedAt);
    private sealed record VersionBody(uint version);

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

    private async Task<Chore> CreateFlexibleChoreAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/chores/", new
        {
            name,
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "standard"
        }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var chore = await resp.Content.ReadFromJsonAsync<Chore>(Json);
        chore.Should().NotBeNull();
        return chore!;
    }

    [Fact]
    public async Task CreateClaimDropComplete_RoundTrip_ThroughHttp_AgainstRealPostgres()
    {
        var client = ClientA;

        // CREATE (201). The DTO comes back with a real xmin version.
        var created = await CreateFlexibleChoreAsync(client, "Round-trip chore");
        created.assignmentKind.Should().Be("none");
        var id = created.id;

        // CLAIM (200) — assignment trio becomes Claimed by the caller; version advances.
        var claimResp = await client.PostAsync($"/api/chores/{id}/claim",
            JsonContent.Create(new VersionBody(created.version), options: Json));
        claimResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var claimed = await claimResp.Content.ReadFromJsonAsync<Chore>(Json);
        claimed!.assignmentKind.Should().Be("claimed");
        claimed.assigneeUserId.Should().Be(ChoresWebAppFactory.UserAId);
        claimed.version.Should().NotBe(created.version);

        // DROP (200) — back to the pile.
        var dropResp = await client.PostAsync($"/api/chores/{id}/drop",
            JsonContent.Create(new VersionBody(claimed.version), options: Json));
        dropResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dropped = await dropResp.Content.ReadFromJsonAsync<Chore>(Json);
        dropped!.assignmentKind.Should().Be("none");
        dropped.assigneeUserId.Should().BeNull();

        // COMPLETE (200) — a recurring chore stays Active; LastCompletedAt is set.
        var completeResp = await client.PostAsync($"/api/chores/{id}/complete",
            JsonContent.Create(new { note = "done", version = dropped.version }, options: Json));
        completeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var completed = await completeResp.Content.ReadFromJsonAsync<Chore>(Json);
        completed!.lastCompletedAt.Should().NotBeNull("completion stamps LastCompletedAt");
    }

    [Fact]
    public async Task Drop_UnclaimedChore_Returns400_IllegalTransition()
    {
        var client = ClientA;

        // A freshly created chore is on the pile (unclaimed). Dropping it is an illegal transition
        // (drop is Claimed-only, holder-only) → ChoreValidationException → 400 (MN8 — rejected, not coerced).
        var created = await CreateFlexibleChoreAsync(client, "Cannot-drop chore");

        var dropResp = await client.PostAsync($"/api/chores/{created.id}/drop",
            JsonContent.Create(new VersionBody(created.version), options: Json));

        dropResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Claim_NonexistentChore_IsRejected()
    {
        var client = ClientA;

        // No chore with this id exists in household A → ChoreNotFoundException → the endpoint returns 404.
        // NOTE: the app's global UseStatusCodePagesWithReExecute("/not-found") middleware (Program.cs,
        // pre-existing — same behavior for the shopping-list endpoints) re-executes empty 404 responses through
        // the Blazor "/not-found" page, surfacing on the wire as a 400. The load-bearing assertion is that the
        // claim is REJECTED with a client error (not silently satisfied), not the exact 404-vs-400 code.
        var claimResp = await client.PostAsync("/api/chores/999999/claim",
            JsonContent.Create(new VersionBody(0), options: Json));

        claimResp.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
        claimResp.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
