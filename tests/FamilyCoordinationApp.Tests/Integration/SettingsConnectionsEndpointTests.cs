using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end coverage of the Settings island B endpoints (<c>/api/settings/connections</c>) through the real
/// HTTP pipeline against real Postgres (reuses <see cref="ChoresWebAppFactory"/>'s two-household seed — household
/// A id=1 with alice + amy, household B id=2 with bob). Each test method gets its OWN freshly-seeded database, so
/// mutations are isolated. Proves: the generate→get→cancel invite lifecycle; the validate outcome envelope
/// (valid / self / already-connected / bad code, all 200 — review §8); accept connects both sides symmetrically;
/// disconnect removes it both sides and is idempotent; the 401 gate; and the M1 invariant that a third household
/// cannot sever a pairing it isn't part of (review R-B2).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class SettingsConnectionsEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        await _factory.EnsureSeededAsync();
        // The connection service's rate-limiter is a process-static dictionary keyed by household id (shared
        // across every integration test in the process). Clear it so a prior test's validate-failures can't
        // rate-limit this one (the seed household ids 1/2 are reused across methods).
        HouseholdConnectionService.ClearRateLimitState();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient ClientB => _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

    private const string Url = "/api/settings/connections";

    // Wire shapes (camelCase via JsonSerializerDefaults.Web).
    private sealed record InviteDto(string code, string expiresAt);
    private sealed record ConnectedDto(int householdId, string householdName, string connectedAt);
    private sealed record ConnectionsDto(InviteDto? activeInvite, List<ConnectedDto> connected);
    private sealed record ValidateResult(bool isValid, string? householdName, string? error);
    private sealed record AcceptResult(bool success, string? connectedHouseholdName, string? error);

    private async Task<InviteDto> GenerateInviteAsync(HttpClient client)
    {
        var resp = await client.PostAsync($"{Url}/invite", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<InviteDto>(Json))!;
    }

    private async Task<ConnectionsDto> GetConnectionsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ConnectionsDto>($"{Url}/", Json))!;

    // ─── Gate + load ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Connections_Unauthenticated_Returns401()
    {
        var resp = await _factory.CreateAnonymousClient().GetAsync($"{Url}/");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Connections_Empty_HasNoActiveInvite_AndNoConnections()
    {
        var dto = await GetConnectionsAsync(ClientA);
        dto.activeInvite.Should().BeNull();
        dto.connected.Should().BeEmpty();
    }

    // ─── Invite lifecycle ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Invite_GenerateGetCancel_RoundTrips_AndCancelIsIdempotent()
    {
        var invite = await GenerateInviteAsync(ClientA);
        invite.code.Should().HaveLength(6);
        DateTime.Parse(invite.expiresAt).ToUniversalTime().Should().BeAfter(DateTime.UtcNow);

        // GET reflects the active invite.
        var after = await GetConnectionsAsync(ClientA);
        after.activeInvite.Should().NotBeNull();
        after.activeInvite!.code.Should().Be(invite.code);

        // Cancel → 204 → no active invite.
        var del = await ClientA.DeleteAsync($"{Url}/invite");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetConnectionsAsync(ClientA)).activeInvite.Should().BeNull();

        // Cancel again with no active invite is an idempotent 204 (double-click safe).
        (await ClientA.DeleteAsync($"{Url}/invite")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Invite_Generate_ReplacesPriorActiveInvite()
    {
        var first = await GenerateInviteAsync(ClientA);
        var second = await GenerateInviteAsync(ClientA);

        // Only the latest is active (parity: generate invalidates any prior — GetActiveInvite returns one).
        var dto = await GetConnectionsAsync(ClientA);
        dto.activeInvite!.code.Should().Be(second.code);

        // The superseded code no longer validates for another household.
        var v = await ClientB.PostAsJsonAsync($"{Url}/validate", new { code = first.code }, Json);
        (await v.Content.ReadFromJsonAsync<ValidateResult>(Json))!.isValid.Should().BeFalse();
    }

    // ─── Validate (200 outcome envelope) ──────────────────────────────────────────────

    [Fact]
    public async Task Validate_SelfCode_IsInvalid_WithNonEmptyError()
    {
        var invite = await GenerateInviteAsync(ClientA);

        var resp = await ClientA.PostAsJsonAsync($"{Url}/validate", new { code = invite.code }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var v = (await resp.Content.ReadFromJsonAsync<ValidateResult>(Json))!;
        v.isValid.Should().BeFalse("a household can't connect to its own code (self-connection)");
        v.error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Validate_BadCode_IsInvalid_WithNonEmptyError()
    {
        var resp = await ClientB.PostAsJsonAsync($"{Url}/validate", new { code = "ZZZ999" }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var v = (await resp.Content.ReadFromJsonAsync<ValidateResult>(Json))!;
        v.isValid.Should().BeFalse();
        v.error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Validate_ValidCode_ReturnsValid_WithInvitingHouseholdName()
    {
        var invite = await GenerateInviteAsync(ClientA);

        var resp = await ClientB.PostAsJsonAsync($"{Url}/validate", new { code = invite.code }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var v = (await resp.Content.ReadFromJsonAsync<ValidateResult>(Json))!;
        v.isValid.Should().BeTrue();
        v.householdName.Should().Be("Household A");
        v.error.Should().BeNull();
    }

    // ─── Accept + connected list + disconnect ─────────────────────────────────────────

    [Fact]
    public async Task Accept_Connects_BothHouseholdsSeeEachOther_ThenDisconnectRemovesBothSides()
    {
        var invite = await GenerateInviteAsync(ClientA);

        var acc = await ClientB.PostAsJsonAsync($"{Url}/accept", new { code = invite.code }, Json);
        acc.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await acc.Content.ReadFromJsonAsync<AcceptResult>(Json))!;
        result.success.Should().BeTrue();
        result.connectedHouseholdName.Should().Be("Household A");

        // B sees A; A sees B (the connection is symmetric).
        var bConn = await GetConnectionsAsync(ClientB);
        bConn.connected.Should().ContainSingle()
            .Which.Should().Match<ConnectedDto>(c => c.householdId == ChoresWebAppFactory.HouseholdAId && c.householdName == "Household A");

        var aConn = await GetConnectionsAsync(ClientA);
        aConn.connected.Should().ContainSingle()
            .Which.householdId.Should().Be(ChoresWebAppFactory.HouseholdBId);

        // B disconnects from A → 204 → gone on BOTH sides.
        var dis = await ClientB.DeleteAsync($"{Url}/connected/{ChoresWebAppFactory.HouseholdAId}");
        dis.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetConnectionsAsync(ClientB)).connected.Should().BeEmpty();
        (await GetConnectionsAsync(ClientA)).connected.Should().BeEmpty();

        // Disconnecting an already-gone pairing is an idempotent 204.
        (await ClientB.DeleteAsync($"{Url}/connected/{ChoresWebAppFactory.HouseholdAId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Accept_BadCode_ReturnsFailureEnvelope()
    {
        var acc = await ClientB.PostAsJsonAsync($"{Url}/accept", new { code = "ZZZ999" }, Json);
        acc.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await acc.Content.ReadFromJsonAsync<AcceptResult>(Json))!;
        result.success.Should().BeFalse();
        result.error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Validate_And_Accept_AlreadyConnected_AreRejected()
    {
        // Connect A ↔ B first.
        var invite = await GenerateInviteAsync(ClientA);
        var acc = await ClientB.PostAsJsonAsync($"{Url}/accept", new { code = invite.code }, Json);
        (await acc.Content.ReadFromJsonAsync<AcceptResult>(Json))!.success.Should().BeTrue();

        // A fresh code now reads as already-connected for B (validate + accept both refuse).
        var invite2 = await GenerateInviteAsync(ClientA);

        var v = await ClientB.PostAsJsonAsync($"{Url}/validate", new { code = invite2.code }, Json);
        (await v.Content.ReadFromJsonAsync<ValidateResult>(Json))!.isValid.Should().BeFalse();

        var acc2 = await ClientB.PostAsJsonAsync($"{Url}/accept", new { code = invite2.code }, Json);
        (await acc2.Content.ReadFromJsonAsync<AcceptResult>(Json))!.success.Should().BeFalse();
    }

    [Fact]
    public async Task Disconnect_ThirdParty_CannotSeverAPairingItIsNotPartOf_M1()
    {
        // Connect A ↔ B.
        var invite = await GenerateInviteAsync(ClientA);
        (await (await ClientB.PostAsJsonAsync($"{Url}/accept", new { code = invite.code }, Json))
            .Content.ReadFromJsonAsync<AcceptResult>(Json))!.success.Should().BeTrue();

        // Seed a third household + whitelisted user, authenticate as them.
        var carolEmail = await SeedThirdHouseholdAsync();
        var clientC = _factory.CreateClientAs(carolEmail);

        // C asks to disconnect A and B — the service only acts on a pairing involving the CALLER's household, so
        // these no-op to 204 and leave the A↔B connection intact (M1; one arg is always the resolved caller).
        (await clientC.DeleteAsync($"{Url}/connected/{ChoresWebAppFactory.HouseholdAId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await clientC.DeleteAsync($"{Url}/connected/{ChoresWebAppFactory.HouseholdBId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await GetConnectionsAsync(ClientA)).connected.Select(c => c.householdId)
            .Should().Contain(ChoresWebAppFactory.HouseholdBId, "a stranger cannot sever the A↔B pairing");
    }

    /// <summary>
    /// Insert a third household + a whitelisted user directly (the seed resynced the Households/Users identity
    /// sequences past the explicit-id seed, so generated ids continue past 2/3 with no PK collision). Returns the
    /// new user's email for <see cref="ChoresWebAppFactory.CreateClientAs"/>.
    /// </summary>
    private async Task<string> SeedThirdHouseholdAsync()
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var ctx = await dbFactory.CreateDbContextAsync();

        var household = new Household { Name = "Household C", CreatedAt = DateTime.UtcNow };
        ctx.Households.Add(household);
        await ctx.SaveChangesAsync();

        var user = new User
        {
            HouseholdId = household.Id,
            Email = "carol@household-c.test",
            DisplayName = "Carol C",
            Initials = "CC",
            IsWhitelisted = true,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        return user.Email;
    }
}
