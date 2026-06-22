using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The digest-run seam end-to-end on real Postgres + the booted host (WP-08, the v1.1 centerpiece). Covers the
/// header-only shared-secret token auth (E9/M9/MN10), idempotency + the concurrent double-fire race (the real
/// cron failure mode, council C2 — closed by WP-05's atomic <c>ExecuteUpdateAsync</c> claim), multi-tenant
/// isolation of the captured <c>DigestModel</c>, and failure isolation (M10). The bound <see cref="
/// Fakes.FakeDigestSender"/> captures every send — NO real discord.com call ever occurs (failure criterion).
/// <para>
/// All time-dependent behaviour is evaluated against the factory's FIXED clock (<see cref="
/// ChoresWebAppFactory.FixedNowUtc"/>), at which BOTH seeded households are due — so these tests are
/// deterministic, never wall-clock-flaky.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class DigestRunIntegrationTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private const string TokenHeader = "X-Digest-Trigger-Token";

    private sealed record RunSummary(int sent, int skipped, int failed);
    private sealed record BoardChore(int id, uint version);
    private sealed record Board(List<BoardChore> chores);

    [Fact]
    public async Task Run_ExcludesSnoozedChore_FromDigestModel()
    {
        // V11 (digest surface). Snooze household A's only chore, then run the digest: A's captured model must
        // exclude it from FallingBehind AND UpForGrabsCount. Household B (un-snoozed) is the in-test control —
        // its unclaimed chore still counts as up-for-grabs.
        var userClient = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var board = await userClient.GetFromJsonAsync<Board>("/api/chores/board", Json);
        var pile = board!.chores.Single(c => c.id == ChoresWebAppFactory.PileChoreAId);
        var snooze = await userClient.PatchAsync($"/api/chores/{ChoresWebAppFactory.PileChoreAId}/snooze",
            JsonContent.Create(new { days = 5, version = pile.version }, options: Json));
        snooze.StatusCode.Should().Be(HttpStatusCode.OK);

        var runClient = _factory.CreateAnonymousClient();
        var resp = await PostRunAsync(runClient, ChoresWebAppFactory.DigestTriggerToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var aSend = _factory.DigestSender.Invocations.Single(i => i.WebhookUrl == ChoresWebAppFactory.WebhookUrlA);
        aSend.Model.FallingBehind.Should().NotContain("Pile chore (race target)",
            "a snoozed chore is excluded from the digest's falling-behind list");
        aSend.Model.UpForGrabsCount.Should().Be(0, "a snoozed chore is excluded from the digest up-for-grabs count");

        // Control: household B's chore was NOT snoozed, so it still surfaces as up-for-grabs.
        var bSend = _factory.DigestSender.Invocations.Single(i => i.WebhookUrl == ChoresWebAppFactory.WebhookUrlB);
        bSend.Model.UpForGrabsCount.Should().Be(1, "the un-snoozed household-B chore is still up-for-grabs (control)");
    }

    /// <summary>POST /api/chores/digest/run with an optional trigger-token header (no cookie; not authed).</summary>
    private async Task<HttpResponseMessage> PostRunAsync(HttpClient client, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/chores/digest/run");
        if (token is not null)
        {
            req.Headers.Add(TokenHeader, token);
        }

        return await client.SendAsync(req);
    }

    /// <summary>Assert the response is the given status AND carries a non-empty body (the UseStatusCodePages
    /// rewrite quirk would blank an empty 401/503 into the Blazor not-found page — WP-06 returns Results.Json).</summary>
    private static async Task AssertStatusWithJsonBodyAsync(HttpResponseMessage resp, HttpStatusCode expected)
    {
        resp.StatusCode.Should().Be(expected);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace("the error body must be non-empty JSON (UseStatusCodePages quirk)");
        body.Trim().Should().StartWith("{", "the endpoint returns a JSON object, not a rewritten HTML page");
        body.Should().Contain("error");
    }

    // ── Token auth (E9/M9/MN10) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_WithValidTokenHeader_Returns200()
    {
        var client = _factory.CreateAnonymousClient(); // NOT cookie-authed — header token only.

        var resp = await PostRunAsync(client, ChoresWebAppFactory.DigestTriggerToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await resp.Content.ReadFromJsonAsync<RunSummary>(Json);
        summary.Should().NotBeNull();
        // Both seeded households are due at the fixed instant → two sends on the first run.
        summary!.sent.Should().Be(2, "both due households send on the first run");
    }

    [Fact]
    public async Task Run_WithNoTokenHeader_Returns401_WithBody()
    {
        var client = _factory.CreateAnonymousClient();

        var resp = await PostRunAsync(client, token: null);

        await AssertStatusWithJsonBodyAsync(resp, HttpStatusCode.Unauthorized);
        // Nothing was sent — the unauthorized request never reached the orchestrator.
        _factory.DigestSender.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_WithWrongTokenHeader_Returns401_WithBody()
    {
        var client = _factory.CreateAnonymousClient();

        var resp = await PostRunAsync(client, "definitely-not-the-token");

        await AssertStatusWithJsonBodyAsync(resp, HttpStatusCode.Unauthorized);
        _factory.DigestSender.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_WithTokenInQueryStringOnly_Returns401_WithBody()
    {
        // MN10: query-string tokens are IGNORED. A correct token in the query string but absent from the
        // header must still be rejected — proves the endpoint never reads the token from the query string.
        var client = _factory.CreateAnonymousClient();

        var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/chores/digest/run?X-Digest-Trigger-Token={ChoresWebAppFactory.DigestTriggerToken}");
        var resp = await client.SendAsync(req);

        await AssertStatusWithJsonBodyAsync(resp, HttpStatusCode.Unauthorized);
        _factory.DigestSender.Invocations.Should().BeEmpty();
    }

    // ── Idempotency (E10/M10) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_Twice_SameWindow_SendsExactlyOncePerHousehold_SecondRunSkips()
    {
        var client = _factory.CreateAnonymousClient();

        var first = await PostRunAsync(client, ChoresWebAppFactory.DigestTriggerToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstSummary = (await first.Content.ReadFromJsonAsync<RunSummary>(Json))!;

        var second = await PostRunAsync(client, ChoresWebAppFactory.DigestTriggerToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondSummary = (await second.Content.ReadFromJsonAsync<RunSummary>(Json))!;

        // First run: both due households sent. Second run (same fixed window): the claim is already stamped, so
        // both are reported under Skipped, and the fake recorded EXACTLY ONE send per household across BOTH runs.
        firstSummary.sent.Should().Be(2);
        secondSummary.sent.Should().Be(0, "the window was already claimed on the first run");
        secondSummary.skipped.Should().Be(2, "both households are now in the already-sent window");

        _factory.DigestSender.SendCountWhere(u => u == ChoresWebAppFactory.WebhookUrlA)
            .Should().Be(1, "household A's digest was sent exactly once across both runs");
        _factory.DigestSender.SendCountWhere(u => u == ChoresWebAppFactory.WebhookUrlB)
            .Should().Be(1, "household B's digest was sent exactly once across both runs");
        _factory.DigestSender.Invocations.Should().HaveCount(2, "exactly two sends total — no double-post");
    }

    // ── Concurrent double-fire (council C2 — the real cron failure mode) ───────────────────────────

    [Fact]
    public async Task Run_TwoConcurrentTriggers_SameInstant_SendExactlyOncePerHousehold()
    {
        // Two cron hits arrive at the SAME fixed instant (the real overlap failure mode). The atomic
        // ExecuteUpdateAsync claim (WP-05) must serialize them: for each household exactly one run's UPDATE
        // matches one row and sends; the loser matches zero (claimed != 1) and skips. Without WP-05's atomic
        // claim this is a read-send-stamp race and the fake would record a double-post.
        var client1 = _factory.CreateAnonymousClient();
        var client2 = _factory.CreateAnonymousClient();

        var t1 = PostRunAsync(client1, ChoresWebAppFactory.DigestTriggerToken);
        var t2 = PostRunAsync(client2, ChoresWebAppFactory.DigestTriggerToken);
        var responses = await Task.WhenAll(t1, t2);

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        var summaries = new List<RunSummary>();
        foreach (var r in responses)
        {
            summaries.Add((await r.Content.ReadFromJsonAsync<RunSummary>(Json))!);
        }

        // EXACTLY ONE send per household TOTAL across the two concurrent runs (the load-bearing assertion).
        _factory.DigestSender.SendCountWhere(u => u == ChoresWebAppFactory.WebhookUrlA)
            .Should().Be(1, "the atomic claim serializes the two concurrent runs for household A");
        _factory.DigestSender.SendCountWhere(u => u == ChoresWebAppFactory.WebhookUrlB)
            .Should().Be(1, "the atomic claim serializes the two concurrent runs for household B");
        _factory.DigestSender.Invocations.Should().HaveCount(2, "two households, one send each — never a double-fire");

        // The two runs' Sent counts sum to exactly 2 (the total real sends); the rest are Skipped (losers of
        // the per-household claim race), and nothing Failed.
        summaries.Sum(s => s.sent).Should().Be(2);
        summaries.Sum(s => s.failed).Should().Be(0);
    }

    // ── Multi-tenant isolation: each captured model reflects only its own household ────────────────

    [Fact]
    public async Task Run_CapturesPerHouseholdModel_NoCrossBleed()
    {
        var client = _factory.CreateAnonymousClient();

        var resp = await PostRunAsync(client, ChoresWebAppFactory.DigestTriggerToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var invocations = _factory.DigestSender.Invocations;
        invocations.Should().HaveCount(2);

        var aSend = invocations.Single(i => i.WebhookUrl == ChoresWebAppFactory.WebhookUrlA);
        var bSend = invocations.Single(i => i.WebhookUrl == ChoresWebAppFactory.WebhookUrlB);

        // Household A seed: Alice 2×Standard (4 pts) + Amy 1×Quick (1 pt) = 3 completions / 5 pts, 2 members.
        aSend.Model.TotalCompletions.Should().Be(3, "household A has exactly its own three completions");
        aSend.Model.TotalPoints.Should().Be(5, "household A's own effort total — no bleed from B");
        aSend.Model.CollectiveHeadline.Should().Contain("Household A");
        aSend.Model.Distribution.Should().Contain(m => m.DisplayName == "Alice A" && m.Points == 4);
        aSend.Model.Distribution.Should().Contain(m => m.DisplayName == "Amy A" && m.Points == 1);
        aSend.Model.Distribution.Should().NotContain(m => m.DisplayName == "Bob B", "B's member must not bleed into A");

        // Household B seed: Bob 1×BigJob (3 pts) = 1 completion / 3 pts, 1 member.
        bSend.Model.TotalCompletions.Should().Be(1, "household B has exactly its own single completion");
        bSend.Model.TotalPoints.Should().Be(3, "household B's own effort total — no bleed from A");
        bSend.Model.CollectiveHeadline.Should().Contain("Household B");
        bSend.Model.Distribution.Should().Contain(m => m.DisplayName == "Bob B" && m.Points == 3);
        bSend.Model.Distribution.Should().NotContain(
            m => m.DisplayName == "Alice A" || m.DisplayName == "Amy A", "A's members must not bleed into B");
    }

    // ── Failure isolation (M10) ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_OneHouseholdSenderThrows_OtherStillSends_AndFailedHouseholdLastSentAtRestored()
    {
        // The sender throws for household B's webhook URL (distinguished at the boundary by URL — the sender
        // never sees a HouseholdId, council). Household A must still deliver; the run summary reports the
        // failure WITHOUT aborting; and B's LastSentAt is compensated back to null so a later tick retries.
        _factory.DigestSender.ThrowForUrl = url => url == ChoresWebAppFactory.WebhookUrlB;

        var client = _factory.CreateAnonymousClient();
        var resp = await PostRunAsync(client, ChoresWebAppFactory.DigestTriggerToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = (await resp.Content.ReadFromJsonAsync<RunSummary>(Json))!;

        summary.sent.Should().BeGreaterThanOrEqualTo(1, "household A still sends despite B failing");
        summary.failed.Should().BeGreaterThanOrEqualTo(1, "household B's send threw and is counted Failed");

        // Household A recorded a successful send; B threw (no successful record for B's URL).
        _factory.DigestSender.SendCountWhere(u => u == ChoresWebAppFactory.WebhookUrlA)
            .Should().Be(1, "the non-failing household still delivers");
        _factory.DigestSender.SendCountWhere(u => u == ChoresWebAppFactory.WebhookUrlB)
            .Should().Be(0, "the failing household's send threw — never recorded as delivered");
        _factory.DigestSender.ThrownUrls.Should().Contain(ChoresWebAppFactory.WebhookUrlB);

        // COMPENSATION (WP-05/M10): B's claim was rolled back, so its LastSentAt is null again (the pre-claim
        // value) — proving a later run could retry. A's LastSentAt IS stamped (it succeeded). Read raw rows.
        var dbFactory = new PostgresDbContextFactory(_factory.ConnectionString);
        await using var ctx = await dbFactory.CreateDbContextAsync();

        var rowB = await ctx.ChoreDigestSettings.SingleAsync(s => s.HouseholdId == ChoresWebAppFactory.HouseholdBId);
        rowB.LastSentAt.Should().BeNull("the failed household's LastSentAt is compensated back for retry");

        var rowA = await ctx.ChoreDigestSettings.SingleAsync(s => s.HouseholdId == ChoresWebAppFactory.HouseholdAId);
        rowA.LastSentAt.Should().NotBeNull("the household that delivered keeps its stamped LastSentAt");
    }
}

/// <summary>
/// The refuse-if-unconfigured (503) path for <c>POST /api/chores/digest/run</c>: a host whose trigger token is
/// UNCONFIGURED must reject the run with 503 + a non-empty JSON body (never the empty-body→Blazor-page rewrite),
/// even when a syntactically valid-looking token header is presented. Uses a dedicated factory subclass that
/// clears the token. (Separate class so it can carry its own host variant under the shared container.)
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class DigestRunUnconfiguredTokenTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly UnconfiguredDigestWebAppFactory _factory = new(postgres);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Run_WhenTriggerTokenUnconfigured_Returns503_WithBody()
    {
        var client = _factory.CreateAnonymousClient();

        // Even presenting "a" token header — with the feature unconfigured the endpoint short-circuits to 503.
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/chores/digest/run");
        req.Headers.Add("X-Digest-Trigger-Token", "anything");
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace("the 503 must carry a JSON body (UseStatusCodePages quirk)");
        body.Trim().Should().StartWith("{");
        body.Should().Contain("error");

        // Nothing was sent — the feature is off.
        _factory.DigestSender.Invocations.Should().BeEmpty();
    }
}
