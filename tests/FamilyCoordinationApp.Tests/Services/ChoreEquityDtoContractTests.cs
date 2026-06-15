using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit tests for the equity DTO (council C1 / M7). Serializes a representative
/// <see cref="ChoreEquityDto"/> with the SAME <see cref="JsonSerializerOptions"/> WP-06 registers globally
/// (camelCase properties + <see cref="JsonStringEnumConverter"/> with camelCase naming, council M5) and
/// asserts it matches the checked-in <c>Fixtures/ChoreEquity/equity.json</c> fixture EXACTLY. The island's
/// <c>types.ts</c> mirrors this fixture; any DTO shape/casing change (field rename, enum casing drift,
/// added/removed property) breaks this test → forcing the island contract to update in lockstep (M7).
/// </summary>
public class ChoreEquityDtoContractTests
{
    /// <summary>
    /// The canonical equity-DTO serialization options. WP-06 MUST register an equivalent
    /// <see cref="JsonStringEnumConverter"/> (camelCase) globally so the HTTP responses match this fixture.
    /// </summary>
    public static readonly JsonSerializerOptions EquityJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ChoreEquity", "equity.json");

    /// <summary>
    /// A representative equity DTO for a 3-member household covering: non-round percents (41.7 / 33.3 / 25)
    /// to verify the PERCENT 0–100 scale (not 0–1 fraction); a non-null <c>pictureUrl</c> on one member
    /// and <c>null</c> on others; non-zero <c>fallingBehindCount</c> and <c>upForGrabsCount</c> (these are
    /// endpoint-side fields, carried here to confirm the DTO shape); <c>window</c> as the echoed string.
    /// Static values only — no clocks — so the serialization is byte-deterministic.
    /// </summary>
    public static ChoreEquityDto BuildRepresentativeEquity()
    {
        // 3 members, total 12 effort points:
        //   Alice:  5 pts, 2 completions → sharePct = round(100 * 5/12, 1) = 41.7
        //   Bob:    4 pts, 1 completion  → sharePct = round(100 * 4/12, 1) = 33.3
        //   Carol:  3 pts, 1 completion  → sharePct = round(100 * 3/12, 1) = 25.0
        //   equalSharePct = round(100/3, 1) = 33.3
        // These specific values appear in equity.json to prove the 0–100 scale is unambiguous.
        var members = new List<MemberShareDto>
        {
            new(
                UserId: 1,
                DisplayName: "Alice",
                Initials: "AL",
                PictureUrl: "https://example.com/alice.png",
                Points: 5,
                Completions: 2,
                SharePct: 41.7),
            new(
                UserId: 2,
                DisplayName: "Bob",
                Initials: "BO",
                PictureUrl: null,
                Points: 4,
                Completions: 1,
                SharePct: 33.3),
            new(
                UserId: 3,
                DisplayName: "Carol",
                Initials: "CA",
                PictureUrl: null,
                Points: 3,
                Completions: 1,
                SharePct: 25.0),
        };

        // Planning footprint (Phase 15): per-member, all-time, un-blended labeled tallies. Hand-picked so
        // Alice leads every lane, Bob trails, and Carol is an all-zero row (proving zero rows still appear).
        // These exact values appear in equity.json.
        var planning = new List<MemberPlanningDto>
        {
            new(
                UserId: 1,
                DisplayName: "Alice",
                ChoresSetUp: 19,
                RecipesAdded: 6,
                ListItemsCurated: 28,
                HandOffs: 4),
            new(
                UserId: 2,
                DisplayName: "Bob",
                ChoresSetUp: 3,
                RecipesAdded: 2,
                ListItemsCurated: 5,
                HandOffs: 1),
            new(
                UserId: 3,
                DisplayName: "Carol",
                ChoresSetUp: 0,
                RecipesAdded: 0,
                ListItemsCurated: 0,
                HandOffs: 0),
        };

        return new ChoreEquityDto(
            Window: "week",
            TotalPoints: 12,
            TotalCompletions: 4,
            EqualSharePct: 33.3,
            FallingBehindCount: 2,
            UpForGrabsCount: 3,
            Members: members)
        {
            Planning = planning,
        };
    }

    [Fact]
    public void SerializedEquity_MatchesContractFixture()
    {
        var equity = BuildRepresentativeEquity();

        var actualJson = JsonSerializer.Serialize(equity, EquityJsonOptions);

        File.Exists(FixturePath).Should().BeTrue(
            $"the equity.json contract fixture must be checked in at {FixturePath}");
        var expectedJson = File.ReadAllText(FixturePath);

        // Compare via parsed JSON nodes (whitespace/line-ending tolerant) so the assertion is about
        // the contract shape + values, not file formatting.
        Normalize(actualJson).Should().Be(Normalize(expectedJson),
            "the serialized equity DTO must match the checked-in contract fixture; if this fails after a "
            + "deliberate DTO change, update equity.json AND the island types.ts in lockstep (M7)");
    }

    [Fact]
    public void SerializedEquity_UsesCamelCaseKeys()
    {
        var equity = BuildRepresentativeEquity();
        var json = JsonSerializer.Serialize(equity, EquityJsonOptions);
        var root = JsonNode.Parse(json)!.AsObject();

        // Top-level camelCase property set is frozen (M7 — added/removed keys break this).
        root.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "window", "totalPoints", "totalCompletions", "equalSharePct",
            "fallingBehindCount", "upForGrabsCount", "members", "planning");

        var firstMember = root["members"]!.AsArray()[0]!.AsObject();
        firstMember.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "userId", "displayName", "initials", "pictureUrl", "points", "completions", "sharePct");

        // Per-member planning object key set is frozen too (Phase 15 — mirrored by island types.ts).
        var firstPlanning = root["planning"]!.AsArray()[0]!.AsObject();
        firstPlanning.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "userId", "displayName", "choresSetUp", "recipesAdded", "listItemsCurated", "handOffs");

        // sharePct and equalSharePct are PERCENT 0–100, not fractions 0–1.
        root["equalSharePct"]!.GetValue<double>().Should().BeGreaterThan(1.0,
            "equalSharePct is PERCENT (33.3), not fraction (0.333)");

        firstMember["sharePct"]!.GetValue<double>().Should().BeGreaterThan(1.0,
            "sharePct is PERCENT (41.7), not fraction (0.417)");
    }

    /// <summary>A deliberate rename of any contract field must break the fixture test (tripwire).</summary>
    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var equity = BuildRepresentativeEquity();
        var json = JsonSerializer.Serialize(equity, EquityJsonOptions);

        // Simulate a drift: rename "sharePct" → "sharePercent". The mutated payload must NOT match the fixture.
        var drifted = json.Replace("\"sharePct\"", "\"sharePercent\"");
        var expectedJson = File.ReadAllText(FixturePath);

        Normalize(drifted).Should().NotBe(Normalize(expectedJson));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
