using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit tests for the ledger DTO (Phase 15 WP-04, M5). Serializes a representative
/// <see cref="ChoreLedgerDto"/> with the SAME options WP-06 registers globally (camelCase +
/// <see cref="JsonStringEnumConverter"/> camelCase) and asserts it matches the checked-in
/// <c>Fixtures/ChoreHistory/ledger.json</c> EXACTLY. The island's <c>types.ts</c> mirrors this fixture; any
/// shape/casing change breaks this test → forcing the island contract to update in lockstep. Also enforces
/// the neutral-framing invariant: NO <c>userId</c>/<c>^id$</c>/mention key anywhere (D9/MN1).
/// </summary>
public class ChoreLedgerDtoContractTests
{
    public static readonly JsonSerializerOptions LedgerJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ChoreHistory", "ledger.json");

    /// <summary>
    /// A representative ledger covering: a note+photo event and a plain event; an EMPTY week alongside a
    /// populated one (the weave scaffold includes empty weeks); a slipped and a snoozed ghost; a gone-quiet
    /// entry with a <c>null</c> <c>lastCompletedLocalDate</c> (never completed) and one with a date. Static
    /// values only — no clocks — so serialization is byte-deterministic.
    /// </summary>
    public static ChoreLedgerDto BuildRepresentativeLedger() => new(
        WindowStartLocal: "2026-04-06",
        WindowEndLocal: "2026-06-24",
        Events: new List<LedgerEventDto>
        {
            new("Dishes", "Alice", "2026-06-22", 2, "Left them sparkling", true),
            new("Take out trash", "Bob", "2026-06-21", 1, null, false),
        },
        Weeks: new List<LedgerWeekDto>
        {
            new("2026-06-08", 0), // empty week — still present in the scaffold
            new("2026-06-15", 2),
        },
        Ghosts: new List<GhostDto>
        {
            new("Water plants", "2026-06-17", "slipped"),
            new("Take out trash", "2026-06-18", "snoozed"),
        },
        GoneQuiet: new List<GoneQuietDto>
        {
            new("Water plants", "every 7 days", null, "slipped"),   // never completed → null
            new("Deep clean", "Sun", "2026-03-15", "snoozed"),
        });

    [Fact]
    public void SerializedLedger_MatchesContractFixture()
    {
        var ledger = BuildRepresentativeLedger();

        var actualJson = JsonSerializer.Serialize(ledger, LedgerJsonOptions);

        File.Exists(FixturePath).Should().BeTrue(
            $"the ledger.json contract fixture must be checked in at {FixturePath}");
        var expectedJson = File.ReadAllText(FixturePath);

        Normalize(actualJson).Should().Be(Normalize(expectedJson),
            "the serialized ledger DTO must match the checked-in contract fixture; if this fails after a "
            + "deliberate DTO change, update ledger.json AND the island types.ts in lockstep (M5)");
    }

    [Fact]
    public void SerializedLedger_UsesCamelCaseKeys()
    {
        var ledger = BuildRepresentativeLedger();
        var json = JsonSerializer.Serialize(ledger, LedgerJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        root.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "windowStartLocal", "windowEndLocal", "events", "weeks", "ghosts", "goneQuiet");

        root["events"]!.AsArray()[0]!.AsObject().Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "choreName", "doerDisplayName", "localDate", "points", "note", "hasPhoto");
        root["weeks"]!.AsArray()[0]!.AsObject().Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "weekStartLocal", "completions");
        root["ghosts"]!.AsArray()[0]!.AsObject().Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "choreName", "expectedLocalDate", "reason");
        root["goneQuiet"]!.AsArray()[0]!.AsObject().Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "choreName", "cadenceLabel", "lastCompletedLocalDate", "reason");
    }

    /// <summary>
    /// The neutral-framing tripwire (D9/MN1): NO <c>userId</c>/<c>^id$</c>/mention key appears anywhere in the
    /// serialized ledger. Asserted structurally so a future field addition can't silently reintroduce identity.
    /// </summary>
    [Fact]
    public void SerializedLedger_HasNoUserIdOrMentionKeys()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeLedger(), LedgerJsonOptions);
        var offending = new List<string>();
        CollectOffendingKeys(JsonNode.Parse(json)!, offending);
        offending.Should().BeEmpty("the ledger carries displayName only — no userId/id/mention (D9/MN1)");
    }

    [Fact]
    public void GoneQuiet_NeverCompleted_SerializesNullLastCompletedDate()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeLedger(), LedgerJsonOptions);
        var firstGoneQuiet = JsonNode.Parse(json)!["goneQuiet"]!.AsArray()[0]!.AsObject();

        firstGoneQuiet.ContainsKey("lastCompletedLocalDate").Should().BeTrue("the key is always present");
        firstGoneQuiet["lastCompletedLocalDate"].Should().BeNull("JSON null = never completed (not a sentinel)");
    }

    /// <summary>A deliberate rename of any contract field must break the fixture test (tripwire).</summary>
    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeLedger(), LedgerJsonOptions);
        var drifted = json.Replace("\"expectedLocalDate\"", "\"expectedDate\"");
        var expectedJson = File.ReadAllText(FixturePath);

        Normalize(drifted).Should().NotBe(Normalize(expectedJson));
    }

    private static void CollectOffendingKeys(JsonNode node, List<string> offending)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    var lower = key.ToLowerInvariant();
                    if (lower is "userid" or "id" || lower.Contains("mention"))
                    {
                        offending.Add(key);
                    }
                    if (value is not null)
                    {
                        CollectOffendingKeys(value, offending);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is not null)
                    {
                        CollectOffendingKeys(item, offending);
                    }
                }
                break;
        }
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
