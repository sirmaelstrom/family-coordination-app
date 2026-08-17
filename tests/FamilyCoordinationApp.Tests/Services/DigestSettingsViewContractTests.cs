using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract test for the digest-settings read view (M9 pin v2). Pins <see cref="DigestSettingsView"/>
/// (GET /api/chores/digest-settings) to <c>Fixtures/Settings/digest-settings.json</c>. Two enums ride
/// this payload — <see cref="DigestCadence"/> and BCL <see cref="DayOfWeek"/> — both camelCase via the
/// app's global converter; their full member lists are pinned by <see cref="WireEnumContractTests"/>.
/// </summary>
public class DigestSettingsViewContractTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Settings", "digest-settings.json");

    /// <summary>A configured household: webhook stored (hint only — never the URL), one digest already sent.</summary>
    public static DigestSettingsView BuildRepresentativeView() => new(
        Enabled: true,
        Cadence: DigestCadence.Weekly,
        SendDayOfWeek: DayOfWeek.Sunday,
        SendHourLocal: 17,
        HasWebhook: true,
        WebhookHint: "…hooks/abcd",
        LastSentAt: new DateTime(2026, 6, 15, 22, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void SerializedView_MatchesContractFixture()
    {
        var actualJson = JsonSerializer.Serialize(BuildRepresentativeView(), Options);

        File.Exists(FixturePath).Should().BeTrue(
            $"the digest-settings.json contract fixture must be checked in at {FixturePath}");

        Normalize(actualJson).Should().Be(Normalize(File.ReadAllText(FixturePath)),
            "the serialized DigestSettingsView must match the checked-in contract fixture; if this fails "
            + "after a deliberate change, update digest-settings.json AND the island types.ts in lockstep (M9)");
    }

    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeView(), Options);
        var drifted = json.Replace("\"webhookHint\"", "\"webhookUrl\"");

        Normalize(drifted).Should().NotBe(Normalize(File.ReadAllText(FixturePath)));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
