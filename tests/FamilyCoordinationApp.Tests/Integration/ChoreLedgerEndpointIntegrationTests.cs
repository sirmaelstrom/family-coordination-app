using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The ledger endpoint (<c>GET /api/chores/ledger</c>) on real Postgres + the booted host. Proves household
/// isolation (a caller sees only their OWN household's completions, M1), the <c>weeks</c> default (12), and
/// that an anonymous caller is rejected with no data leak. This is also the first REAL-Postgres exercise of the
/// WP-01 first-ever <c>MIN(CompletedAt) GROUP BY ChoreId</c> pushdown and the WP-03 snooze-seed aggregation
/// (they must translate + execute, not just pass on the InMemory provider).
/// <para>Aggregation runs against the factory's FIXED clock (local Sun 2026-06-07), whose Mon–Sun week the
/// seeded mid-week completions fall in — so the feed is deterministic.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreLedgerEndpointIntegrationTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record LedgerEvent(string choreName, string doerDisplayName, string localDate, int points, string? note, bool hasPhoto);
    private sealed record LedgerWeek(string weekStartLocal, int completions);
    private sealed record Ghost(string choreName, string expectedLocalDate, string reason);
    private sealed record GoneQuiet(string choreName, string cadenceLabel, string? lastCompletedLocalDate, string reason);
    private sealed record Ledger(
        string windowStartLocal, string windowEndLocal,
        List<LedgerEvent> events, List<LedgerWeek> weeks, List<Ghost> ghosts, List<GoneQuiet> goneQuiet);

    [Fact]
    public async Task Ledger_AsUserA_ReturnsHouseholdAOnly_NoLeak()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var ledger = await client.GetFromJsonAsync<Ledger>("/api/chores/ledger?weeks=4", Json);

        ledger.Should().NotBeNull();
        ledger!.weeks.Should().HaveCount(4, "weeks=4 produces a 4-week scaffold including empty weeks");
        ledger.events.Should().NotBeEmpty("household A has three in-week completions");
        ledger.events.Should().OnlyContain(e => e.doerDisplayName == "Alice A" || e.doerDisplayName == "Amy A");
        ledger.events.Should().NotContain(e => e.doerDisplayName == "Bob B", "household B's member must not appear");
        // The wire is displayName-only — no userId anywhere in the raw payload (D9/MN1).
        var raw = await client.GetStringAsync("/api/chores/ledger?weeks=4");
        raw.Should().NotContain("userId", "the ledger carries no userId (neutral framing)");
        raw.Should().NotContain("Household B private chore", "household B's chore name must not leak");
    }

    [Fact]
    public async Task Ledger_DefaultWeeks_IsTwelve()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var ledger = await client.GetFromJsonAsync<Ledger>("/api/chores/ledger", Json);

        ledger.Should().NotBeNull();
        ledger!.weeks.Should().HaveCount(12, "the ledger weave defaults to a 12-week grid");
    }

    [Fact]
    public async Task Ledger_WeeksClampedToTwentySix()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var ledger = await client.GetFromJsonAsync<Ledger>("/api/chores/ledger?weeks=99", Json);

        ledger!.weeks.Should().HaveCount(26, "weeks is clamped to 26 by the service");
    }

    [Fact]
    public async Task Ledger_AsUserB_SumsOnlyHouseholdB()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

        var ledger = await client.GetFromJsonAsync<Ledger>("/api/chores/ledger?weeks=4", Json);

        ledger.Should().NotBeNull();
        ledger!.events.Should().OnlyContain(e => e.doerDisplayName == "Bob B", "only household B's completion");
        ledger.events.Should().NotContain(e => e.doerDisplayName == "Alice A" || e.doerDisplayName == "Amy A");
    }

    [Fact]
    public async Task Ledger_Anonymous_IsRejected_NoLeak()
    {
        var client = _factory.CreateAnonymousClient();

        var resp = await client.GetAsync("/api/chores/ledger?weeks=4");

        ((int)resp.StatusCode).Should().BeInRange(400, 499, "an anonymous caller must be rejected");
        resp.StatusCode.Should().NotBe(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("Alice A", "no ledger data may leak to an unauthenticated caller");
        body.Should().NotContain("Bob B");
    }
}
