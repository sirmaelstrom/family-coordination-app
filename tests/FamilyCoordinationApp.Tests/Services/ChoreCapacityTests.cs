using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// The capacity doctrine's truth table (migrated from the SPA's capacity-fit tests — quest e7f8be86)
/// plus the ladder pin: <c>Fixtures/ChoreCapacity/capacity-ladder.json</c> is byte-compared to the
/// module's own output here, and the SPA's capacity-fit tests hold the TS mirror list-equal to the
/// same fixture — a drifted copy fails CI on whichever side drifted.
/// </summary>
public class ChoreCapacityTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ChoreCapacity", "capacity-ladder.json");

    /// <summary>The pinned tier rows: the three real tiers plus the unset (null) column.</summary>
    private static readonly (string Key, string? Tier)[] Rows =
    [
        (CapacityTier.Full, CapacityTier.Full),
        (CapacityTier.Reduced, CapacityTier.Reduced),
        (CapacityTier.Minimal, CapacityTier.Minimal),
        ("unset", null),
    ];

    public static object BuildLadder() => new
    {
        showsFitsMe = Rows.ToDictionary(r => r.Key, r => ChoreCapacity.ShowsFitsMe(r.Tier)),
        fitSets = Rows.ToDictionary(
            r => r.Key,
            r => ChoreCapacity.FitSetFor(r.Tier).Select(e => e.ToString()).ToArray()),
        weights = Rows.ToDictionary(r => r.Key, r => ChoreCapacity.WeightFor(r.Tier)),
    };

    [Fact]
    public void Ladder_MatchesContractFixture()
    {
        var actualJson = JsonSerializer.Serialize(BuildLadder(), new JsonSerializerOptions { WriteIndented = true });

        File.Exists(FixturePath).Should().BeTrue(
            $"the capacity-ladder.json contract fixture must be checked in at {FixturePath}");

        Normalize(actualJson).Should().Be(Normalize(File.ReadAllText(FixturePath)),
            "the capacity doctrine must match the checked-in ladder; if this fails after a deliberate "
            + "change, update capacity-ladder.json AND the SPA's capacity-fit mirror in lockstep");
    }

    // ── The truth table (the R4′ founding case first) ────────────────────────

    [Theory]
    [InlineData(CapacityTier.Full)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bogus")]
    public void FitsMeChip_NeverShowsForFullOrUnset(string? tier)
    {
        // The founding-case guarantee: a Full/unset viewer is never shown a per-person affordance.
        ChoreCapacity.ShowsFitsMe(tier).Should().BeFalse();
    }

    [Theory]
    [InlineData(CapacityTier.Reduced)]
    [InlineData(CapacityTier.Minimal)]
    public void FitsMeChip_ShowsOnlyForTheWhitelistedTiers(string tier)
    {
        ChoreCapacity.ShowsFitsMe(tier).Should().BeTrue();
    }

    [Fact]
    public void FitSets_AreExact()
    {
        ChoreCapacity.FitSetFor(CapacityTier.Minimal).Should().Equal(EffortTier.Quick);
        ChoreCapacity.FitSetFor(CapacityTier.Reduced).Should().Equal(EffortTier.Quick, EffortTier.Standard);
        ChoreCapacity.FitSetFor(CapacityTier.Full).Should().Equal(EffortTier.Quick, EffortTier.Standard, EffortTier.BigJob);
        ChoreCapacity.FitSetFor(null).Should().Equal(EffortTier.Quick, EffortTier.Standard, EffortTier.BigJob);
    }

    [Fact]
    public void Weights_AreTheEquityNumbers()
    {
        // The exact values ChoreEquityCalculator used before delegating here (Phase 15 D3) — the
        // delegation must be value-identical.
        ChoreCapacity.WeightFor(CapacityTier.Full).Should().Be(1.0);
        ChoreCapacity.WeightFor(CapacityTier.Reduced).Should().Be(0.5);
        ChoreCapacity.WeightFor(CapacityTier.Minimal).Should().Be(0.15);
        ChoreCapacity.WeightFor(null).Should().Be(1.0);
        ChoreCapacity.WeightFor("Bogus").Should().Be(1.0);
    }

    [Fact]
    public void Fits_IsMembershipInTheFitSet()
    {
        ChoreCapacity.Fits(EffortTier.Quick, CapacityTier.Minimal).Should().BeTrue();
        ChoreCapacity.Fits(EffortTier.Standard, CapacityTier.Minimal).Should().BeFalse();
        ChoreCapacity.Fits(EffortTier.BigJob, CapacityTier.Reduced).Should().BeFalse();
        ChoreCapacity.Fits(EffortTier.BigJob, null).Should().BeTrue();
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
