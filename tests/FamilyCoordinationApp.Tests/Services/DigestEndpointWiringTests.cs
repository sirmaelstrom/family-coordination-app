using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Endpoints;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Wiring-level unit tests for the v1.1 chore endpoints (WP-06). These pin the bits that must NOT silently
/// drift but can be exercised without a booted host / WebApplicationFactory (the full HTTP/auth/idempotency
/// assertions are WP-08 on real Postgres):
/// <list type="bullet">
///   <item><description>the cron run-endpoint token check (<see cref="ChoresEndpoints.ValidateTriggerToken"/>):
///   valid / mismatch / missing / unconfigured (M9 — refuse-if-unconfigured + fixed-time compare).</description></item>
///   <item><description>the equity <c>window</c> allowlist (<see cref="ChoresEndpoints.TryParseEquityWindow"/>):
///   week/all/default ok, bogus rejected (M16).</description></item>
///   <item><description>the camelCase serialization of the digest-settings enums (<c>cadence</c>,
///   <c>sendDayOfWeek</c>) so the wire contract can't drift from the island <c>types.ts</c> (WP-11).</description></item>
/// </list>
/// </summary>
public class DigestEndpointWiringTests
{
    // ── Run-endpoint token validation (M9) ────────────────────────────────────────────

    [Fact]
    public void ValidateTriggerToken_ValidMatch_ReturnsTrue()
    {
        ChoresEndpoints.ValidateTriggerToken("s3cret-token", "s3cret-token").Should().BeTrue();
    }

    [Fact]
    public void ValidateTriggerToken_Mismatch_ReturnsFalse()
    {
        ChoresEndpoints.ValidateTriggerToken("s3cret-token", "wrong-token").Should().BeFalse();
    }

    [Fact]
    public void ValidateTriggerToken_DifferentLength_ReturnsFalse()
    {
        // FixedTimeEquals is length-sensitive; a prefix must not pass.
        ChoresEndpoints.ValidateTriggerToken("s3cret-token", "s3cret").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateTriggerToken_MissingPresentedToken_ReturnsFalse(string? presented)
    {
        ChoresEndpoints.ValidateTriggerToken("s3cret-token", presented).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateTriggerToken_Unconfigured_ReturnsFalse(string? configured)
    {
        // Refuse-if-unconfigured: even a "matching" empty presented token must NOT authorize.
        ChoresEndpoints.ValidateTriggerToken(configured, "anything").Should().BeFalse();
        ChoresEndpoints.ValidateTriggerToken(configured, configured).Should().BeFalse();
    }

    // ── Equity window allowlist (M16) ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null, EquityWindow.Week)]
    [InlineData("", EquityWindow.Week)]
    [InlineData("   ", EquityWindow.Week)]
    [InlineData("week", EquityWindow.Week)]
    [InlineData("WEEK", EquityWindow.Week)]
    [InlineData(" Week ", EquityWindow.Week)]
    [InlineData("all", EquityWindow.All)]
    [InlineData("ALL", EquityWindow.All)]
    public void TryParseEquityWindow_KnownOrDefault_Parses(string? input, EquityWindow expected)
    {
        ChoresEndpoints.TryParseEquityWindow(input, out var parsed).Should().BeTrue();
        parsed.Should().Be(expected);
    }

    [Theory]
    [InlineData("month")]
    [InlineData("weekly")]
    [InlineData("everything")]
    [InlineData("0")]
    public void TryParseEquityWindow_Unknown_Rejected(string input)
    {
        ChoresEndpoints.TryParseEquityWindow(input, out _).Should().BeFalse();
    }

    // ── Tri-state webhook mapping (WP-11 mirrors this) ─────────────────────────────────

    [Fact]
    public void DigestSettingsRequest_WebhookKeep_LeavesUnchanged()
    {
        var req = new ChoresEndpoints.DigestSettingsRequest(
            Enabled: true, Cadence: DigestCadence.Weekly, SendDayOfWeek: DayOfWeek.Sunday,
            SendHourLocal: 18, WebhookAction: "keep", WebhookUrl: "https://ignored");

        var update = req.ToUpdate();
        update.WebhookProvided.Should().BeFalse();
    }

    [Fact]
    public void DigestSettingsRequest_WebhookActionOmitted_LeavesUnchanged()
    {
        var req = new ChoresEndpoints.DigestSettingsRequest(
            Enabled: true, Cadence: DigestCadence.Weekly, SendDayOfWeek: DayOfWeek.Sunday,
            SendHourLocal: 18);

        var update = req.ToUpdate();
        update.WebhookProvided.Should().BeFalse();
    }

    [Fact]
    public void DigestSettingsRequest_WebhookSet_ProvidesUrl()
    {
        var req = new ChoresEndpoints.DigestSettingsRequest(
            Enabled: true, Cadence: DigestCadence.Weekly, SendDayOfWeek: DayOfWeek.Sunday,
            SendHourLocal: 18, WebhookAction: "set", WebhookUrl: "https://discord.com/api/webhooks/x/y");

        var update = req.ToUpdate();
        update.WebhookProvided.Should().BeTrue();
        update.WebhookUrl.Should().Be("https://discord.com/api/webhooks/x/y");
    }

    [Fact]
    public void DigestSettingsRequest_WebhookClear_ProvidesNull()
    {
        var req = new ChoresEndpoints.DigestSettingsRequest(
            Enabled: true, Cadence: DigestCadence.Weekly, SendDayOfWeek: DayOfWeek.Sunday,
            SendHourLocal: 18, WebhookAction: "clear", WebhookUrl: "https://ignored");

        var update = req.ToUpdate();
        update.WebhookProvided.Should().BeTrue();
        update.WebhookUrl.Should().BeNull();
    }

    // ── camelCase enum serialization contract (frozen for WP-11 types.ts) ──────────────

    private static readonly JsonSerializerOptions CamelCaseEnumOptions = BuildCamelCaseEnumOptions();

    private static JsonSerializerOptions BuildCamelCaseEnumOptions()
    {
        // Mirror Program.cs ConfigureHttpJsonOptions: JsonStringEnumConverter(CamelCase).
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    [Fact]
    public void DigestSettingsView_SerializesCadenceAndDayLowercaseCamelCase()
    {
        var view = new DigestSettingsView(
            Enabled: true,
            Cadence: DigestCadence.Weekly,
            SendDayOfWeek: DayOfWeek.Sunday,
            SendHourLocal: 18,
            HasWebhook: false,
            WebhookHint: null,
            LastSentAt: null);

        var json = JsonSerializer.Serialize(view, CamelCaseEnumOptions);

        json.Should().Contain("\"cadence\":\"weekly\"");
        json.Should().Contain("\"sendDayOfWeek\":\"sunday\"");
        json.Should().Contain("\"sendHourLocal\":18");
        // Must NOT leak PascalCase enum names.
        json.Should().NotContain("Weekly");
        json.Should().NotContain("Sunday");
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, "monday")]
    [InlineData(DayOfWeek.Saturday, "saturday")]
    public void DigestSettingsView_SerializesEachDayLowercase(DayOfWeek day, string expected)
    {
        var view = new DigestSettingsView(
            Enabled: false,
            Cadence: DigestCadence.Weekly,
            SendDayOfWeek: day,
            SendHourLocal: 9,
            HasWebhook: false,
            WebhookHint: null,
            LastSentAt: null);

        var json = JsonSerializer.Serialize(view, CamelCaseEnumOptions);
        json.Should().Contain($"\"sendDayOfWeek\":\"{expected}\"");
    }
}
