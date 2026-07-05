using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Contract / consumer-audit tests for the EVOLVED recap DTO (Phase 15 WP-05 — retrofitted; the recap payload
/// had no contract test before). Serializes a representative <see cref="ChoreRecapDto"/> with the SAME options
/// WP-06 registers globally and asserts it matches <c>Fixtures/ChoreRecap/recap.json</c>. Crucially, it also
/// serializes <see cref="ChoreRecapDto.Current"/> ALONE and compares to a frozen <c>recap-current.json</c>
/// baseline — so the digest-mirror block is pinned byte-for-byte INDEPENDENTLY of the outer recap shape (M6:
/// the in-app "This week" must never diverge from the Discord digest).
/// </summary>
public class ChoreRecapDtoContractTests
{
    public static readonly JsonSerializerOptions RecapJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ChoreRecap");
    private static readonly string RecapFixturePath = Path.Combine(FixtureDir, "recap.json");
    private static readonly string CurrentFixturePath = Path.Combine(FixtureDir, "recap-current.json");

    /// <summary>
    /// A representative evolved recap: a digest-mirror <c>Current</c>; a 2-point trend each carrying a per-week
    /// <c>distribution</c> (displayName only); milestones (best week / streak / a first-ever / season totals);
    /// a note+photo kept moment; a room + General what-got-tended; a never-completed gone-quiet entry (null
    /// lastCompletedLocalDate). Static values only — byte-deterministic.
    /// </summary>
    public static ChoreRecapDto BuildRepresentativeRecap() => new(
        Current: new RecapWeekDto(
            WeekStartLocal: "2026-06-15",
            Headline: "The Smith house knocked out 4 chores (10 pts) this week!",
            TotalCompletions: 4,
            TotalPoints: 10,
            Distribution: new List<RecapMemberLineDto>
            {
                new("Alice", 6, 60.0),
                new("Bob", 4, 40.0),
            },
            FallingBehind: new List<string> { "Take out trash" },
            UpForGrabsCount: 2),
        Trend: new List<RecapTrendPointDto>
        {
            new("2026-06-08", 3, 7, false, new List<RecapMemberLineDto> { new("Alice", 4, 57.1), new("Bob", 3, 42.9) }),
            new("2026-06-15", 4, 10, true, new List<RecapMemberLineDto> { new("Alice", 6, 60.0), new("Bob", 4, 40.0) }),
        },
        Milestones: new MilestonesDto(
            BestWeek: new BestWeekDto("2026-06-15", 4, 10),
            LongestActiveStreakWeeks: 2,
            FirstEvers: new List<FirstEverDto> { new("Deep clean", "2026-06-10") },
            SeasonTotalCompletions: 7,
            SeasonTotalPoints: 17),
        KeptMoments: new List<KeptMomentDto>
        {
            new("2026-06-14", "Dishes", "Left them sparkling", true),
        },
        WhatGotTended: new List<WhatGotTendedDto>
        {
            new("Kitchen", 5),
            new("General", 2),
        },
        GoneQuiet: new List<GoneQuietDto>
        {
            new("Water plants", "every 7 days", null, "slipped"),
        });

    [Fact]
    public void SerializedRecap_MatchesContractFixture()
    {
        var actualJson = JsonSerializer.Serialize(BuildRepresentativeRecap(), RecapJsonOptions);

        File.Exists(RecapFixturePath).Should().BeTrue(
            $"the recap.json contract fixture must be checked in at {RecapFixturePath}");
        Normalize(actualJson).Should().Be(Normalize(File.ReadAllText(RecapFixturePath)),
            "the serialized recap DTO must match the checked-in fixture; on a deliberate change, update "
            + "recap.json AND the island types.ts in lockstep (M5)");
    }

    /// <summary>
    /// M6 — the digest-mirror <c>Current</c> block serializes byte-identical to a frozen baseline, verified by
    /// serializing <c>dto.Current</c> ALONE (not a substring of the full recap) so outer shape shifts can't mask
    /// a divergence. If this fails, the in-app "This week" has drifted from the Discord digest — STOP (E3).
    /// </summary>
    [Fact]
    public void Current_SerializesByteIdenticalToFrozenBaseline()
    {
        var currentJson = JsonSerializer.Serialize(BuildRepresentativeRecap().Current, RecapJsonOptions);

        File.Exists(CurrentFixturePath).Should().BeTrue(
            $"the frozen current-block baseline must be checked in at {CurrentFixturePath}");
        Normalize(currentJson).Should().Be(Normalize(File.ReadAllText(CurrentFixturePath)),
            "the digest-mirror Current block must never change (M6/E3) — the in-app view must not diverge from the digest");
    }

    [Fact]
    public void SerializedRecap_UsesCamelCaseKeys_AndDistributionHasNoUserId()
    {
        var root = JsonNode.Parse(JsonSerializer.Serialize(BuildRepresentativeRecap(), RecapJsonOptions))!.AsObject();

        root.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "current", "trend", "milestones", "keptMoments", "whatGotTended", "goneQuiet");

        var firstTrend = root["trend"]!.AsArray()[0]!.AsObject();
        firstTrend.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "weekStartLocal", "totalCompletions", "totalPoints", "isCurrent", "distribution");

        // Per-week distribution is displayName-only — NO userId (D6/MN1).
        var firstDist = firstTrend["distribution"]!.AsArray()[0]!.AsObject();
        firstDist.Select(kvp => kvp.Key).Should().BeEquivalentTo("displayName", "points", "sharePct");
        firstDist.ContainsKey("userId").Should().BeFalse("the per-week distribution carries no userId (MN1)");

        var milestones = root["milestones"]!.AsObject();
        milestones.Select(kvp => kvp.Key).Should().BeEquivalentTo(
            "bestWeek", "longestActiveStreakWeeks", "firstEvers", "seasonTotalCompletions", "seasonTotalPoints");
    }

    [Fact]
    public void RenamingAField_BreaksTheFixture()
    {
        var json = JsonSerializer.Serialize(BuildRepresentativeRecap(), RecapJsonOptions);
        var drifted = json.Replace("\"whatGotTended\"", "\"roomTallies\"");

        Normalize(drifted).Should().NotBe(Normalize(File.ReadAllText(RecapFixturePath)));
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
