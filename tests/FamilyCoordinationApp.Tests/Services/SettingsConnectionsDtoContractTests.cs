using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit test for the Settings island B aggregate DTO (M9 — mirrors
/// <see cref="SettingsDtoContractTests"/>). Serializes a representative <see cref="ConnectionsDto"/> with the SAME
/// options the app registers globally (camelCase, Web defaults) and asserts byte-equality with the checked-in
/// <c>Fixtures/Settings/connections.json</c>. The island's <c>types.ts</c> mirrors this fixture; any DTO
/// shape/casing change breaks this test → forcing the island contract to update in lockstep (M9).
///
/// <para>⚠ DATES (review X5): <see cref="ConnectionInviteDto.ExpiresAt"/> and
/// <see cref="ConnectedFamilyDto.ConnectedAt"/> are FULL INSTANTS (DateTime, UTC) → serialize as round-trip
/// ISO-8601 strings ("2026-06-26T12:00:00Z"), NOT bare "YYYY-MM-DD". The island renders them local via
/// new Date(iso).</para>
/// </summary>
public class SettingsConnectionsDtoContractTests
{
    /// <summary>Web defaults (camelCase) — equivalent to the app's global Minimal-API JSON config for these (enum-free) DTOs.</summary>
    public static readonly JsonSerializerOptions ConnectionsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static string FixturePath(string file) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Settings", file);

    public static ConnectionsDto BuildRepresentativeConnections() => new(
        ActiveInvite: new ConnectionInviteDto(
            Code: "ABC234",
            ExpiresAt: new DateTime(2026, 6, 26, 12, 0, 0, DateTimeKind.Utc)),
        Connected: new List<ConnectedFamilyDto>
        {
            new(HouseholdId: 2, HouseholdName: "The Smiths",
                ConnectedAt: new DateTime(2026, 6, 20, 14, 30, 0, DateTimeKind.Utc)),
        });

    [Fact]
    public void SerializedConnections_MatchesContractFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeConnections(), ConnectionsJsonOptions);
        File.Exists(FixturePath("connections.json")).Should().BeTrue();
        Normalize(json).Should().Be(Normalize(File.ReadAllText(FixturePath("connections.json"))),
            "the serialized ConnectionsDto must match connections.json; update the fixture AND types.ts in lockstep (M9)");
    }

    [Fact]
    public void Connections_UseCamelCaseKeys_AndIsoInstantDates()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeConnections(), ConnectionsJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();
        root.Select(kvp => kvp.Key).Should().BeEquivalentTo("activeInvite", "connected");

        var invite = root["activeInvite"]!.AsObject();
        invite.Select(kvp => kvp.Key).Should().BeEquivalentTo("code", "expiresAt");
        // ExpiresAt is a full ISO-8601 instant (NOT a bare date) — review X5.
        invite["expiresAt"]!.GetValue<string>().Should().Be("2026-06-26T12:00:00Z");

        var connected = root["connected"]!.AsArray()[0]!.AsObject();
        connected.Select(kvp => kvp.Key).Should().BeEquivalentTo("householdId", "householdName", "connectedAt");
        connected["connectedAt"]!.GetValue<string>().Should().Be("2026-06-20T14:30:00Z");
    }

    [Fact]
    public void ActiveInvite_IsNullable()
    {
        // Parity: a household with no active invite serializes activeInvite: null (the island shows the
        // "Create Invite Code" button, not a code).
        var dto = new ConnectionsDto(ActiveInvite: null, Connected: new List<ConnectedFamilyDto>());
        var json = JsonSerializer.Serialize(dto, ConnectionsJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();
        root["activeInvite"].Should().BeNull("a JSON null property reads back as a null JsonNode");
        root["connected"]!.AsArray().Should().BeEmpty();
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
