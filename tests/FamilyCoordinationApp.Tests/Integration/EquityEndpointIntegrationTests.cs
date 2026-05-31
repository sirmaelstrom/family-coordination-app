using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// V3 — the equity distribution endpoint (<c>GET /api/chores/equity</c>) on real Postgres + the booted host.
/// Proves household isolation (each caller sums only their OWN household's effort-weighted completions, M1),
/// the window allowlist (bogus → 400), and that an anonymous caller is rejected with no data leak (the
/// app-global <c>UseStatusCodePagesWithReExecute</c> quirk is accommodated — any 4xx, never a 200/leak).
/// <para>Aggregation is evaluated against the factory's FIXED clock, at whose Mon–Sun week the seeded
/// mid-week completions fall — so the totals are deterministic.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class EquityEndpointIntegrationTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record MemberShare(int userId, string displayName, int points, int completions, double sharePct);
    private sealed record Equity(
        string window, int totalPoints, int totalCompletions, double equalSharePct,
        int fallingBehindCount, int upForGrabsCount, List<MemberShare> members);

    [Fact]
    public async Task Equity_AsUserA_SumsOnlyHouseholdA()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var equity = await client.GetFromJsonAsync<Equity>("/api/chores/equity?window=week", Json);

        equity.Should().NotBeNull();
        equity!.window.Should().Be("week");
        // Household A seed: Alice 2×Standard (4 pts) + Amy 1×Quick (1 pt) = 3 completions / 5 pts.
        equity.totalCompletions.Should().Be(3, "only household A's three in-week completions count");
        equity.totalPoints.Should().Be(5, "only household A's effort points — no bleed from B");
        equity.members.Should().HaveCount(2, "household A has two members (Alice, Amy)");
        equity.members.Should().Contain(m => m.displayName == "Alice A" && m.points == 4 && m.completions == 2);
        equity.members.Should().Contain(m => m.displayName == "Amy A" && m.points == 1 && m.completions == 1);
        equity.members.Should().NotContain(m => m.displayName == "Bob B", "household B's member must not appear");
    }

    [Fact]
    public async Task Equity_AsUserB_SumsOnlyHouseholdB()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

        var equity = await client.GetFromJsonAsync<Equity>("/api/chores/equity?window=week", Json);

        equity.Should().NotBeNull();
        // Household B seed: Bob 1×BigJob (3 pts) = 1 completion / 3 pts.
        equity!.totalCompletions.Should().Be(1, "only household B's single in-week completion counts");
        equity.totalPoints.Should().Be(3, "only household B's effort points — no bleed from A");
        equity.members.Should().ContainSingle().Which.displayName.Should().Be("Bob B");
        equity.members.Should().NotContain(
            m => m.displayName == "Alice A" || m.displayName == "Amy A", "household A's members must not appear");
    }

    [Fact]
    public async Task Equity_DefaultWindow_IsWeek()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        // No window query → defaults to week.
        var equity = await client.GetFromJsonAsync<Equity>("/api/chores/equity", Json);

        equity.Should().NotBeNull();
        equity!.window.Should().Be("week");
        equity.totalCompletions.Should().Be(3);
    }

    [Fact]
    public async Task Equity_WindowAll_SumsAllTime()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var equity = await client.GetFromJsonAsync<Equity>("/api/chores/equity?window=all", Json);

        equity.Should().NotBeNull();
        equity!.window.Should().Be("all");
        // Same three completions are also within the all-time window (no lower bound).
        equity.totalCompletions.Should().Be(3);
        equity.totalPoints.Should().Be(5);
    }

    [Fact]
    public async Task Equity_BogusWindow_Returns400()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var resp = await client.GetAsync("/api/chores/equity?window=fortnight");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an unknown window is rejected, never coerced");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace("the 400 carries a JSON message");
    }

    [Fact]
    public async Task Equity_Anonymous_IsRejected_NoLeak()
    {
        var client = _factory.CreateAnonymousClient();

        var resp = await client.GetAsync("/api/chores/equity?window=week");

        // 4xx (401 challenge from RequireAuthorization; the UseStatusCodePages quirk could surface other 4xx) —
        // the load-bearing property is it is NOT a 200 and leaks no equity data.
        ((int)resp.StatusCode).Should().BeInRange(400, 499, "an anonymous caller must be rejected");
        resp.StatusCode.Should().NotBe(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("totalPoints", "no equity payload may leak to an unauthenticated caller");
        body.Should().NotContain("Alice A");
        body.Should().NotContain("Bob B");
    }
}
