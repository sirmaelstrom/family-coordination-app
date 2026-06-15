using FluentAssertions;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Digest;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Unit tests for the pure <see cref="DigestBuilder"/>. No I/O — the builder is stateless and
/// parameterless-ctor; every test constructs one directly.
/// <para>
/// Framing/contract assertions (M11/M12/MN8):
/// <list type="bullet">
///   <item><see cref="DigestMemberLine"/> has no userId field and no @mention field (structural).</item>
///   <item><see cref="DigestModel"/> has no ranking flag, no targeted-nudge field (structural).</item>
///   <item>Headline is collective (mentions completions/points, never a name).</item>
///   <item>FallingBehind lists chore names, not person names.</item>
/// </list>
/// </para>
/// </summary>
public class DigestBuilderTests
{
    private readonly DigestBuilder _builder = new();

    // ---- Fixture data -------------------------------------------------------------------------------

    private static ChoreEquityResult ThreeMemberEquity() =>
        new(
            TotalPoints: 12,
            TotalCompletions: 4,
            EqualSharePct: 33.3,
            Members:
            [
                new MemberEquityShare(1, "Alice", "AL", null, 5, 2, 41.7),
                new MemberEquityShare(2, "Bob", "BO", null, 4, 1, 33.3),
                new MemberEquityShare(3, "Carol", "CA", null, 3, 1, 25.0),
            ]);

    private static IReadOnlyList<DigestChoreLine> MixedDueness() =>
    [
        new DigestChoreLine("Vacuum", DueState.Overdue),
        new DigestChoreLine("Dishes", DueState.DueToday),
        new DigestChoreLine("Laundry", DueState.NotDue),
        new DigestChoreLine("Mow lawn", DueState.Scheduled),
    ];

    // ---- Totals (V5) -------------------------------------------------------------------------------

    [Fact]
    public void Build_TotalsMatchEquityResult()
    {
        var model = _builder.Build("Heath", ThreeMemberEquity(), MixedDueness(), upForGrabsCount: 3);

        model.TotalCompletions.Should().Be(4);
        model.TotalPoints.Should().Be(12);
        model.UpForGrabsCount.Should().Be(3);
    }

    // ---- Distribution (V5) -------------------------------------------------------------------------

    [Fact]
    public void Build_DistributionContainsAllMembers()
    {
        var model = _builder.Build("Heath", ThreeMemberEquity(), MixedDueness(), upForGrabsCount: 0);

        model.Distribution.Should().HaveCount(3);
        model.Distribution.Select(d => d.DisplayName).Should().Contain(["Alice", "Bob", "Carol"]);
    }

    [Fact]
    public void Build_DistributionCarriesCorrectPointsAndSharePct()
    {
        var model = _builder.Build("Heath", ThreeMemberEquity(), MixedDueness(), upForGrabsCount: 0);

        var alice = model.Distribution.Single(d => d.DisplayName == "Alice");
        alice.Points.Should().Be(5);
        alice.SharePct.Should().Be(41.7);

        var carol = model.Distribution.Single(d => d.DisplayName == "Carol");
        carol.Points.Should().Be(3);
        carol.SharePct.Should().Be(25.0);
    }

    [Fact]
    public void Build_DistributionUnaffectedBy_ExpectedSharePct_DigestByteIdentical()
    {
        // Phase 15 WP-05 (MN2/D1 seam proof): MemberEquityShare gained an additive ExpectedSharePct init
        // property. The digest reads ONLY DisplayName / Points / SharePct by name — setting ExpectedSharePct
        // (the capacity-weighted reference) must NOT change a single digest member line.
        var withoutExpected = ThreeMemberEquity();
        var withExpected = new ChoreEquityResult(
            withoutExpected.TotalPoints,
            withoutExpected.TotalCompletions,
            withoutExpected.EqualSharePct,
            withoutExpected.Members
                // Same positional fields; only the new init property differs (arbitrary non-flat values).
                .Select((m, i) => m with { ExpectedSharePct = 12.3 + i })
                .ToList());

        var baseline = _builder.Build("Heath", withoutExpected, MixedDueness(), upForGrabsCount: 3);
        var withCapacity = _builder.Build("Heath", withExpected, MixedDueness(), upForGrabsCount: 3);

        // The distribution member lines (DisplayName / Points / SharePct) are byte-identical.
        withCapacity.Distribution.Should().BeEquivalentTo(baseline.Distribution,
            "ExpectedSharePct is purely additive — the digest never reads it (MN2/D1)");
        withCapacity.TotalPoints.Should().Be(baseline.TotalPoints);
        withCapacity.CollectiveHeadline.Should().Be(baseline.CollectiveHeadline);
    }

    [Fact]
    public void Build_DistributionOrderedAlphabetically()
    {
        // Members arrive in points-desc order from the equity result (Alice, Bob, Carol).
        // Distribution must be alphabetical (neutral, not a ranking).
        var model = _builder.Build("Heath", ThreeMemberEquity(), MixedDueness(), upForGrabsCount: 0);

        var names = model.Distribution.Select(d => d.DisplayName).ToList();
        names.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase,
            "distribution order must be alphabetical, not by points — no ranking framing");
    }

    // ---- FallingBehind (V5) ------------------------------------------------------------------------

    [Fact]
    public void Build_FallingBehind_ContainsOnlyOverdueAndDueToday()
    {
        var model = _builder.Build("Heath", ThreeMemberEquity(), MixedDueness(), upForGrabsCount: 0);

        // "Vacuum" (Overdue) and "Dishes" (DueToday) should appear; "Laundry" and "Mow lawn" should not.
        model.FallingBehind.Should().Contain("Vacuum");
        model.FallingBehind.Should().Contain("Dishes");
        model.FallingBehind.Should().NotContain("Laundry");
        model.FallingBehind.Should().NotContain("Mow lawn");
    }

    [Fact]
    public void Build_FallingBehind_IsEmpty_WhenNoChoreDue()
    {
        var allFresh = new List<DigestChoreLine>
        {
            new("Vacuum", DueState.NotDue),
            new("Dishes", DueState.Scheduled),
        };

        var model = _builder.Build("Heath", ThreeMemberEquity(), allFresh, upForGrabsCount: 0);

        model.FallingBehind.Should().BeEmpty();
    }

    // ---- Headline (non-punitive, collective) -------------------------------------------------------

    [Fact]
    public void Build_Headline_IsCollective_ContainsCountAndPoints()
    {
        var model = _builder.Build("Heath", ThreeMemberEquity(), MixedDueness(), upForGrabsCount: 0);

        model.CollectiveHeadline.Should().Contain("4");  // total completions
        model.CollectiveHeadline.Should().Contain("12"); // total points
    }

    [Fact]
    public void Build_Headline_DoesNotContainAnyMemberName()
    {
        var model = _builder.Build("Heath", ThreeMemberEquity(), MixedDueness(), upForGrabsCount: 0);

        // Headline is collective — never names an individual (M12/MN8).
        model.CollectiveHeadline.Should().NotContain("Alice");
        model.CollectiveHeadline.Should().NotContain("Bob");
        model.CollectiveHeadline.Should().NotContain("Carol");
    }

    [Fact]
    public void Build_Headline_DoesNotContainAtMention()
    {
        var model = _builder.Build("Heath", ThreeMemberEquity(), MixedDueness(), upForGrabsCount: 0);

        model.CollectiveHeadline.Should().NotContain("@",
            "headline must never contain a Discord @mention (M11/MN8)");
    }

    // ---- Non-punitive structural contract (V5) ---------------------------------------------------

    [Fact]
    public void DigestMemberLine_HasNoUserIdField()
    {
        // Structural assertion: DigestMemberLine must not expose a userId or mention-target field.
        var memberLineType = typeof(DigestMemberLine);

        memberLineType.GetProperty("UserId").Should().BeNull(
            "DigestMemberLine must not expose a UserId field — no targeting/mention vector (M11/MN8)");
        memberLineType.GetProperty("Mention").Should().BeNull(
            "DigestMemberLine must not expose a Mention field (M11/MN8)");
        memberLineType.GetProperty("DiscordId").Should().BeNull(
            "DigestMemberLine must not expose a DiscordId field (M11/MN8)");
    }

    [Fact]
    public void DigestModel_HasNoRankingOrTargetingField()
    {
        var modelType = typeof(DigestModel);

        modelType.GetProperty("TopPerformer").Should().BeNull(
            "DigestModel must not have a TopPerformer ranking field (M12)");
        modelType.GetProperty("LaggingBehind").Should().BeNull(
            "DigestModel must not have a LaggingBehind person-targeting field (MN8)");
        modelType.GetProperty("TargetedNudges").Should().BeNull(
            "DigestModel must not have a TargetedNudges field (MN8)");
    }

    // ---- Zero-activity household (edge case) -------------------------------------------------------

    [Fact]
    public void Build_ZeroCompletions_HeadlineStillCollective_NoException()
    {
        var empty = new ChoreEquityResult(0, 0, 33.3,
        [
            new MemberEquityShare(1, "Alice", "AL", null, 0, 0, 0.0),
        ]);

        var act = () => _builder.Build("Test", empty, [], upForGrabsCount: 0);

        act.Should().NotThrow();
        var model = act();
        model.TotalCompletions.Should().Be(0);
        model.TotalPoints.Should().Be(0);
        model.FallingBehind.Should().BeEmpty();
        model.CollectiveHeadline.Should().Contain("0");
    }

    // ---- Singular/plural grammar in headline -------------------------------------------------------

    [Fact]
    public void Build_SingleCompletion_HeadlineSingular()
    {
        var single = new ChoreEquityResult(2, 1, 100.0,
        [
            new MemberEquityShare(1, "Alice", "AL", null, 2, 1, 100.0),
        ]);

        var model = _builder.Build("Test", single, [], upForGrabsCount: 0);

        model.CollectiveHeadline.Should().Contain("1 chore (");
        model.CollectiveHeadline.Should().NotContain("chores");
    }
}
