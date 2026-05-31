using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// V4 — the digest-settings endpoints (<c>GET/PUT /api/chores/digest-settings</c>) on real Postgres + the
/// booted host. Proves the webhook is encrypted over the wire and at rest: a PUT with
/// <c>webhookAction:"set"</c> stores ciphertext (raw row ≠ plaintext, read via <see cref="
/// PostgresDbContextFactory"/>), the GET view reports <c>hasWebhook:true</c> with the URL ABSENT from the
/// response body (MN7), and <c>webhookAction:"clear"</c> flips it back to <c>hasWebhook:false</c>.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class DigestSettingsIntegrationTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // A distinct webhook URL set BY this test (not the seeded one) so the ciphertext/absence assertions are
    // unambiguous about what was just stored.
    private const string PutWebhookUrl = "https://discord.com/api/webhooks/2000000000000000009/PUT-TEST-SECRET-WXYZ";

    private sealed record SettingsView(
        bool enabled, string cadence, string sendDayOfWeek, int sendHourLocal,
        bool hasWebhook, string? webhookHint, DateTime? lastSentAt);

    [Fact]
    public async Task PutWebhook_Set_ThenGet_ReportsHasWebhook_UrlAbsentFromBody_CiphertextAtRest()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        // PUT with webhookAction:"set" — store/encrypt the new URL (camelCase enums on the wire).
        var putResp = await client.PutAsJsonAsync("/api/chores/digest-settings", new
        {
            enabled = true,
            cadence = "weekly",
            sendDayOfWeek = "sunday",
            sendHourLocal = 18,
            webhookAction = "set",
            webhookUrl = PutWebhookUrl
        }, Json);

        putResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // The PUT response (a DigestSettingsView) must NOT echo the URL anywhere (MN7).
        var putBody = await putResp.Content.ReadAsStringAsync();
        putBody.Should().NotContain(PutWebhookUrl, "the response view must never echo the webhook URL (MN7)");
        putBody.Should().NotContain("PUT-TEST-SECRET", "not even a fragment of the URL may appear");

        // GET reports hasWebhook=true; the URL is absent; the hint is at most the last 4 chars.
        var getResp = await client.GetAsync("/api/chores/digest-settings");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = await getResp.Content.ReadAsStringAsync();
        getBody.Should().NotContain(PutWebhookUrl, "the GET view must never contain the webhook URL (MN7)");

        var view = JsonSerializer.Deserialize<SettingsView>(getBody, Json)!;
        view.hasWebhook.Should().BeTrue();
        view.enabled.Should().BeTrue();
        view.cadence.Should().Be("weekly");
        view.sendDayOfWeek.Should().Be("sunday");
        view.webhookHint.Should().Be(PutWebhookUrl[^4..], "the hint is only the last 4 chars of the URL");

        // Raw-row assertion: WebhookUrlProtected is CIPHERTEXT — never the plaintext URL (M8).
        var dbFactory = new PostgresDbContextFactory(_factory.ConnectionString);
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var row = await ctx.ChoreDigestSettings.SingleAsync(s => s.HouseholdId == ChoresWebAppFactory.HouseholdAId);

        row.WebhookUrlProtected.Should().NotBeNullOrEmpty("a webhook is stored");
        row.WebhookUrlProtected.Should().NotBe(PutWebhookUrl, "the stored value must be ciphertext, never plaintext (M8)");
        row.WebhookUrlProtected.Should().NotContain("PUT-TEST-SECRET", "no plaintext fragment may persist at rest");
    }

    [Fact]
    public async Task PutWebhook_Clear_FlipsHasWebhookFalse_AndNullsCiphertext()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        // First set a webhook, then clear it.
        await client.PutAsJsonAsync("/api/chores/digest-settings", new
        {
            enabled = true,
            cadence = "weekly",
            sendDayOfWeek = "sunday",
            sendHourLocal = 18,
            webhookAction = "set",
            webhookUrl = PutWebhookUrl
        }, Json);

        var clearResp = await client.PutAsJsonAsync("/api/chores/digest-settings", new
        {
            enabled = false,
            cadence = "weekly",
            sendDayOfWeek = "sunday",
            sendHourLocal = 18,
            webhookAction = "clear"
        }, Json);

        clearResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = (await clearResp.Content.ReadFromJsonAsync<SettingsView>(Json))!;
        view.hasWebhook.Should().BeFalse("clear removes the stored webhook");
        view.webhookHint.Should().BeNull();

        // Raw row: ciphertext is nulled.
        var dbFactory = new PostgresDbContextFactory(_factory.ConnectionString);
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var row = await ctx.ChoreDigestSettings.SingleAsync(s => s.HouseholdId == ChoresWebAppFactory.HouseholdAId);
        row.WebhookUrlProtected.Should().BeNull("clear nulls the stored ciphertext");
    }

    [Fact]
    public async Task PutSettings_IsHouseholdScoped_DoesNotAffectOtherHousehold()
    {
        // UserA clears their webhook; household B's seeded webhook must be untouched (M1 isolation).
        var clientA = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        await clientA.PutAsJsonAsync("/api/chores/digest-settings", new
        {
            enabled = false,
            cadence = "weekly",
            sendDayOfWeek = "sunday",
            sendHourLocal = 18,
            webhookAction = "clear"
        }, Json);

        var clientB = _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);
        var bView = (await (await clientB.GetAsync("/api/chores/digest-settings"))
            .Content.ReadFromJsonAsync<SettingsView>(Json))!;

        bView.hasWebhook.Should().BeTrue("household B's seeded webhook must be unaffected by A's clear");
    }

    [Fact]
    public async Task AddChoreDigestSettingsMigration_AppliesCleanly_OnTheV10Chain()
    {
        // V11 — the host already booted via MigrateAsync against the fresh container, so the WP-01 v1.1
        // migration must have applied on top of the v1.0 Phase10Chores chain. Assert it precisely.
        var dbFactory = new PostgresDbContextFactory(_factory.ConnectionString);
        await using var ctx = await dbFactory.CreateDbContextAsync();

        var applied = await ctx.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(m => m.Contains("Phase10Chores"), "the v1.0 chore migration is on the chain");
        applied.Should().Contain(m => m.Contains("AddChoreDigestSettings"),
            "the v1.1 additive digest-settings migration must apply on the v1.0 chain");

        (await ctx.Database.GetPendingMigrationsAsync()).Should().BeEmpty("no migration is left pending");

        // The new table is live and queryable (the seeded settings rows exist) — proves the migration worked.
        (await ctx.ChoreDigestSettings.CountAsync()).Should().Be(2, "both seeded households have a settings row");
    }
}
