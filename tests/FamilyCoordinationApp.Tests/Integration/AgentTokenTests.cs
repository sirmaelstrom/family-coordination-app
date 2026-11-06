using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Authorization;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Personal access tokens for agents (quest 8bfa42cf, design §7 PR 1) through the real host and Postgres: mint once,
/// hash-only storage, the bearer gate on <c>/mcp</c> (wrong, malformed, expired, revoked, disabled or moved member),
/// the cookie/bearer separation in both directions, read/write scope separation and the caller tenant the token
/// resolves to.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class AgentTokenTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentQueue<RecordingTenantContext> _tenants = new();
    private ChoresWebAppFactory _factory = null!;

    private sealed record Minted(int id, string token, string label, string safePrefix, string access, DateTime createdAt, DateTime expiresAt);
    private sealed record TokenRow(int id, string label, string safePrefix, string access, string status, DateTime? lastUsedAt, DateTime? revokedAt);
    private sealed record TokenList(string householdName, int[] expiryPresetDays, TokenRow[] tokens);

    public async Task InitializeAsync()
    {
        _factory = new ChoresWebAppFactory(postgres)
        {
            McpEnabled = true,
            TenantContextDecorator = (inner, sp) =>
            {
                var recording = new RecordingTenantContext(
                    inner, sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Path.Value);
                _tenants.Enqueue(recording);
                return recording;
            },
        };
        await _factory.EnsureSeededAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient ClientA2 => _factory.CreateClientAs(ChoresWebAppFactory.UserA2Email);
    private HttpClient ClientB => _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

    private async Task<Minted> MintAsync(HttpClient client, string access = "readWrite", string label = "Claude Code", int days = 365)
    {
        var response = await client.PostAsJsonAsync("/api/agent-tokens", new { label, access, expiresInDays = days });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<Minted>(Json))!;
    }

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static Task<HttpResponseMessage> CallMcp(HttpClient client) =>
        client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });

    private ApplicationDbContext RawContext() =>
        new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContext();

    // ── Mint / list / storage ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mint_returns_the_secret_once_and_stores_only_its_hash()
    {
        var minted = await MintAsync(ClientA, label: "Codex on flagg");

        minted.token.Should().MatchRegex("^fca_pat_[A-Za-z0-9_-]{43}$");
        minted.safePrefix.Should().Be(minted.token[..12]);
        minted.access.Should().Be("readWrite");
        (minted.expiresAt - minted.createdAt).Should().Be(TimeSpan.FromDays(365));

        var listJson = await (await ClientA.GetAsync("/api/agent-tokens")).Content.ReadAsStringAsync();
        listJson.Should().NotContain(minted.token);
        var list = JsonSerializer.Deserialize<TokenList>(listJson, Json)!;
        list.householdName.Should().Be("Household A");
        list.expiryPresetDays.Should().Equal(30, 90, 365);
        list.tokens.Should().ContainSingle(t => t.id == minted.id)
            .Which.Should().Match<TokenRow>(t => t.label == "Codex on flagg" && t.status == "active" && t.safePrefix == minted.safePrefix);

        await using var context = RawContext();
        var stored = await context.PersonalAccessTokens.SingleAsync(t => t.Id == minted.id);
        stored.TokenHash.Should().MatchRegex("^[0-9A-F]{64}$").And.NotContain(minted.token);
        stored.HouseholdId.Should().Be(ChoresWebAppFactory.HouseholdAId);
        stored.UserId.Should().Be(ChoresWebAppFactory.UserAId);
        listJson.Should().NotContain(stored.TokenHash);
    }

    [Fact]
    public async Task Mint_needs_a_fresh_sign_in()
    {
        var stale = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        stale.DefaultRequestHeaders.Add(ChoresWebAppFactory.TestSignInAgeHeader, "1200"); // 20 minutes ago

        var response = await stale.PostAsJsonAsync("/api/agent-tokens", new { label = "x", access = "read", expiresInDays = 30 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain(FreshSignIn.RequiredCode);
        // Listing and revoking need no fresh sign-in: stopping a leaked token must never wait on one.
        (await stale.GetAsync("/api/agent-tokens")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("""{"label":"x","access":"admin","expiresInDays":30}""")]
    [InlineData("""{"label":"x","access":"read","expiresInDays":45}""")]
    [InlineData("""{"label":"x","access":"read"}""")]
    [InlineData("""{"label":"x","access":"read","expiresInDays":30,"expiresOn":"2026-07-01"}""")]
    [InlineData("""{"label":"x","access":"read","expiresOn":"2026-06-07"}""")]   // the frozen clock's today
    [InlineData("""{"label":"x","access":"read","expiresOn":"2027-06-09"}""")]   // more than 365 days out
    [InlineData("""{"label":"   ","access":"read","expiresInDays":30}""")]
    public async Task Mint_rejects_bad_requests_with_a_message(string body)
    {
        var response = await ClientA.PostAsync("/api/agent-tokens", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"message\"");
    }

    [Fact]
    public async Task A_custom_expiry_runs_to_the_end_of_that_local_day()
    {
        var response = await ClientA.PostAsJsonAsync("/api/agent-tokens", new { label = "dated", access = "read", expiresOn = "2026-07-01" });
        var minted = (await response.Content.ReadFromJsonAsync<Minted>(Json))!;

        // America/Chicago in July is UTC-5, so the end of 2026-07-01 local is 2026-07-02 05:00Z.
        minted.expiresAt.ToUniversalTime().Should().Be(new DateTime(2026, 7, 2, 5, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task The_last_allowed_custom_date_never_exceeds_365_days()
    {
        // Review r1 on #163: at 2026-06-07T23:30Z the end of local 2027-06-07 is 2027-06-08T05:00Z, 365 d 5.5 h out.
        var response = await ClientA.PostAsJsonAsync("/api/agent-tokens", new { label = "max", access = "read", expiresOn = "2027-06-07" });
        var minted = (await response.Content.ReadFromJsonAsync<Minted>(Json))!;

        (minted.expiresAt - minted.createdAt).Should().Be(TimeSpan.FromDays(365));
    }

    [Fact]
    public async Task An_active_token_stays_listed_however_much_history_follows_it()
    {
        // Review r1 on #163: 50 newer revoked tokens pushed a still-working one out of the list, so it could not be revoked.
        var survivor = await MintAsync(ClientA, "read", label: "survivor");
        for (var i = 0; i < 51; i++)
        {
            var churn = await MintAsync(ClientA, "read", label: $"churn {i}");
            (await ClientA.DeleteAsync($"/api/agent-tokens/{churn.id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        var list = (await ClientA.GetFromJsonAsync<TokenList>("/api/agent-tokens", Json))!;

        list.tokens.Should().ContainSingle(t => t.id == survivor.id && t.status == "active");
        list.tokens.Count(t => t.status == "revoked").Should().Be(50, "history stays bounded");
    }

    // ── The /mcp gate ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_valid_token_passes_the_gate_as_its_own_member_and_household()
    {
        var a = await MintAsync(ClientA, "read");
        var b = await MintAsync(ClientB, "readWrite");

        (await CallMcp(Bearer(a.token))).StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        (await CallMcp(Bearer(b.token))).StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        var mcpTenants = _tenants.Where(t => t.RequestPath == "/mcp" && t.State == TenantState.Caller)
            .Select(t => (t.HouseholdId, t.UserId)).ToList();
        mcpTenants.Should().BeEquivalentTo(new[]
        {
            ((int?)ChoresWebAppFactory.HouseholdAId, (int?)ChoresWebAppFactory.UserAId),
            ((int?)ChoresWebAppFactory.HouseholdBId, (int?)ChoresWebAppFactory.UserBId),
        });

        await using var context = RawContext();
        (await context.PersonalAccessTokens.SingleAsync(t => t.Id == a.id)).LastUsedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-token")]
    [InlineData("fca_pat_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // well formed, never minted
    [InlineData("fca_pat_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA!!")] // bad alphabet
    public async Task Missing_malformed_or_unknown_tokens_are_refused_with_401_json(string? token)
    {
        var client = token is null ? _factory.CreateAnonymousClient() : Bearer(token);

        var response = await CallMcp(client);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().Contain(h => h.Scheme == "Bearer");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"message\"");
    }

    [Fact]
    public async Task Two_authorization_headers_are_refused()
    {
        var minted = await MintAsync(ClientA, "read");
        var client = _factory.CreateAnonymousClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { }) };
        request.Headers.TryAddWithoutValidation("Authorization", new[] { $"Bearer {minted.token}", $"Bearer {minted.token}" });

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_revoked_token_is_refused_on_its_next_call()
    {
        var minted = await MintAsync(ClientA, "readWrite");
        var agent = Bearer(minted.token);
        (await CallMcp(agent)).StatusCode.Should().Be(HttpStatusCode.NotImplemented);

        (await ClientA.DeleteAsync($"/api/agent-tokens/{minted.id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await CallMcp(agent)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // Revoking again is the state the member wanted.
        (await ClientA.DeleteAsync($"/api/agent-tokens/{minted.id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var list = (await ClientA.GetFromJsonAsync<TokenList>("/api/agent-tokens", Json))!;
        list.tokens.Single(t => t.id == minted.id).status.Should().Be("revoked");
    }

    [Fact]
    public async Task A_member_cannot_revoke_or_see_another_members_token()
    {
        var amys = await MintAsync(ClientA2, "read");
        var bobs = await MintAsync(ClientB, "read");

        (await ClientA.DeleteAsync($"/api/agent-tokens/{amys.id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ClientA.DeleteAsync($"/api/agent-tokens/{bobs.id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var aliceList = (await ClientA.GetFromJsonAsync<TokenList>("/api/agent-tokens", Json))!;
        aliceList.tokens.Should().NotContain(t => t.id == amys.id || t.id == bobs.id);

        (await CallMcp(Bearer(amys.token))).StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        (await CallMcp(Bearer(bobs.token))).StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var minted = await MintAsync(ClientA, "read", days: 30);
        await using (var context = RawContext())
        {
            await context.PersonalAccessTokens.Where(t => t.Id == minted.id)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.ExpiresAt, ChoresWebAppFactory.FixedNowUtc));
        }

        (await CallMcp(Bearer(minted.token))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var list = (await ClientA.GetFromJsonAsync<TokenList>("/api/agent-tokens", Json))!;
        list.tokens.Single(t => t.id == minted.id).status.Should().Be("expired");
    }

    [Fact]
    public async Task A_disabled_member_or_one_who_moved_household_loses_the_token()
    {
        var minted = await MintAsync(ClientA2, "readWrite");
        var agent = Bearer(minted.token);
        try
        {
            await SetMember(ChoresWebAppFactory.UserA2Id, whitelisted: false, householdId: ChoresWebAppFactory.HouseholdAId);
            (await CallMcp(agent)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            await SetMember(ChoresWebAppFactory.UserA2Id, whitelisted: true, householdId: ChoresWebAppFactory.HouseholdBId);
            (await CallMcp(agent)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            await SetMember(ChoresWebAppFactory.UserA2Id, whitelisted: true, householdId: ChoresWebAppFactory.HouseholdAId);
            (await CallMcp(agent)).StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        }
        finally
        {
            await SetMember(ChoresWebAppFactory.UserA2Id, whitelisted: true, householdId: ChoresWebAppFactory.HouseholdAId);
        }
    }

    private async Task SetMember(int userId, bool whitelisted, int householdId)
    {
        await using var context = RawContext();
        await context.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.IsWhitelisted, whitelisted)
            .SetProperty(x => x.HouseholdId, householdId));
    }

    // ── Cookie and bearer never stand in for each other ─────────────────────────────────────

    [Fact]
    public async Task A_signed_in_browser_session_cannot_call_mcp()
    {
        (await CallMcp(ClientA)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_opens_nothing_on_api_including_token_management()
    {
        var minted = await MintAsync(ClientA, "readWrite");
        var agent = Bearer(minted.token);

        (await agent.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await agent.GetAsync("/api/agent-tokens")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await agent.PostAsJsonAsync("/api/agent-tokens", new { label = "self-minted", access = "readWrite", expiresInDays = 365 }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await agent.DeleteAsync($"/api/agent-tokens/{minted.id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await agent.GetAsync("/api/settings/members/")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await agent.GetAsync("/api/settings/household-requests")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Read / write separation ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("read", true, false)]
    [InlineData("readWrite", true, true)]
    public async Task Scopes_follow_the_access_the_member_chose(string access, bool canRead, bool canWrite)
    {
        var minted = await MintAsync(ClientA, access);

        await using var scope = _factory.Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Headers.Authorization = $"Bearer {minted.token}";
        var authenticated = await http.AuthenticateAsync(AgentAccess.Scheme);
        authenticated.Succeeded.Should().BeTrue();

        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        (await authorization.AuthorizeAsync(authenticated.Principal!, AgentAccess.ReadPolicy)).Succeeded.Should().Be(canRead);
        (await authorization.AuthorizeAsync(authenticated.Principal!, AgentAccess.WritePolicy)).Succeeded.Should().Be(canWrite);
    }

    [Fact]
    public async Task A_cookie_principal_never_satisfies_the_agent_policies()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [
                new(AgentAccess.ScopeClaim, AgentAccess.ReadScope),
                new(AgentAccess.ScopeClaim, AgentAccess.WriteScope),
                new(AgentAccess.UserIdClaim, "1"),
                new(AgentAccess.HouseholdIdClaim, "1"),
                new(AgentAccess.TokenIdClaim, "1"),
            ],
            "Cookies"));

        // The forged claims never become a token identity: the authentication type is checked too.
        AgentAccess.ReadIdentity(principal).Should().BeNull();
    }
}

/// <summary>The agent endpoint is not mapped unless <c>Mcp:Enabled</c> is set: off, a valid token gets a 404.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class AgentEndpointDisabledTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Mcp_is_404_when_the_feature_is_off()
    {
        var mint = await _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail)
            .PostAsJsonAsync("/api/agent-tokens", new { label = "x", access = "read", expiresInDays = 30 });
        var token = (await mint.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var client = _factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Unmapped: no route answers, so neither the token gate (401) nor the endpoint (501) runs. A POST to an unmapped
        // path meets the app's GET-only fallback routes as 405.
        (await client.GetAsync("/mcp")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/mcp", new { })).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}
