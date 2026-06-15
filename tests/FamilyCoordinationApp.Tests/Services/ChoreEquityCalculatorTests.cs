using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Unit tests for the pure, timezone-aware <see cref="ChoreEquityCalculator"/>. Every test injects an
/// explicit <c>now</c> (UTC) and an explicit <see cref="TimeZoneInfo"/> — the calculator NEVER reaches
/// for <see cref="DateTime.UtcNow"/>, so these are fully deterministic. No DB, no I/O.
/// </summary>
public class ChoreEquityCalculatorTests
{
    private readonly ChoreEquityCalculator _calc = new();

    // America/Chicago: CDT (UTC-5) in summer. Used for local-week-boundary test where UTC-midnight
    // and local-midnight / local-Monday fall on DIFFERENT calendar dates.
    private static readonly TimeZoneInfo Chicago = ResolveTz("America/Chicago", "Central Standard Time");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static TimeZoneInfo ResolveTz(string ianaId, string windowsId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (TimeZoneNotFoundException)
        {
            // Windows hosts may only know the Windows id (CI runners differ).
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
    }

    private static DateTime Utc0(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static ChoreCompletion Completion(int userId, DateTime completedAt, int effortPoints) =>
        new()
        {
            HouseholdId = 1,
            ChoreId = 1,
            CompletionId = 0,
            CompletedByUserId = userId,
            CompletedAt = completedAt,
            EffortPointsSnapshot = effortPoints
        };

    private static MemberDto Member(int userId, string name) =>
        new(userId, name, name[..2].ToUpperInvariant(), PictureUrl: null);

    // ---- Effort-weighting (V1) — sum EffortPointsSnapshot, not raw count -------------------------

    [Fact]
    public void Compute_WeighsByEffortPoints_NotCompletionCount()
    {
        // Alice: 1 completion at 5 pts; Bob: 3 completions at 1 pt each.
        // If weighted correctly: Alice 5/8 = 62.5%, Bob 3/8 = 37.5%.
        // If weighted by count: Alice 1/4 = 25%, Bob 3/4 = 75% (wrong).
        var now = Utc0(2026, 6, 15, 12); // Monday
        var completions = new[]
        {
            Completion(userId: 1, Utc0(2026, 6, 15, 10), effortPoints: 5),
            Completion(userId: 2, Utc0(2026, 6, 15, 10), effortPoints: 1),
            Completion(userId: 2, Utc0(2026, 6, 15, 11), effortPoints: 1),
            Completion(userId: 2, Utc0(2026, 6, 15, 12), effortPoints: 1),
        };
        var members = new[] { Member(1, "Alice"), Member(2, "Bob") };

        var result = _calc.Compute(completions, members, EquityWindow.Week, now, Utc);

        result.TotalPoints.Should().Be(8);
        result.TotalCompletions.Should().Be(4);

        var alice = result.Members.Single(m => m.UserId == 1);
        var bob = result.Members.Single(m => m.UserId == 2);

        alice.Points.Should().Be(5);
        alice.SharePct.Should().Be(62.5);

        bob.Points.Should().Be(3);
        bob.SharePct.Should().Be(37.5);
    }

    // ---- Multi-person credit (D5 — full points to EACH contributor) --------------------------------

    [Fact]
    public void Compute_MultiPersonChore_FullPointsToEachContributor()
    {
        // D5: a multi-person (co-sign) chore writes one ChoreCompletion per contributor, each snapshotting the
        // chore's FULL EffortPoints (no split). Two distinct contributors of a BigJob (3 pts) each get +3, so
        // the household total is +6. The calculator is unchanged — it just sums the two rows.
        var now = Utc0(2026, 6, 15, 12); // Monday
        var completions = new[]
        {
            Completion(userId: 1, Utc0(2026, 6, 15, 10), effortPoints: 3),
            Completion(userId: 2, Utc0(2026, 6, 15, 11), effortPoints: 3),
        };
        var members = new[] { Member(1, "Alice"), Member(2, "Bob") };

        var result = _calc.Compute(completions, members, EquityWindow.Week, now, Utc);

        result.TotalPoints.Should().Be(6, "full points to each of the two contributors (3 + 3)");
        result.TotalCompletions.Should().Be(2);

        var alice = result.Members.Single(m => m.UserId == 1);
        var bob = result.Members.Single(m => m.UserId == 2);
        alice.Points.Should().Be(3);
        bob.Points.Should().Be(3);
        alice.SharePct.Should().Be(50.0);
        bob.SharePct.Should().Be(50.0);
    }

    // ---- Monday week boundary (council MAJOR) -------------------------------------------------------

    [Fact]
    public void Compute_Week_MondayBoundary_SundayCompletionExcluded_MondayCompletionIncluded()
    {
        // Week window is current Mon–Sun. Evaluate on Wednesday 2026-06-17 (UTC).
        // Monday of that week = 2026-06-15 00:00 UTC.
        //   - Sunday 2026-06-14 23:59 UTC → outside the window → excluded.
        //   - Monday 2026-06-15 00:01 UTC → inside the window → included.
        var now = Utc0(2026, 6, 17, 12); // Wednesday

        var sundayCompletion = Completion(userId: 1, Utc0(2026, 6, 14, 23, 59), effortPoints: 3);
        var mondayCompletion = Completion(userId: 1, Utc0(2026, 6, 15, 0, 1), effortPoints: 5);
        var members = new[] { Member(1, "Alice") };

        var result = _calc.Compute(
            new[] { sundayCompletion, mondayCompletion },
            members,
            EquityWindow.Week,
            now,
            Utc);

        result.TotalPoints.Should().Be(5, "Sunday completion is outside the Mon–Sun window");
        result.TotalCompletions.Should().Be(1);

        var alice = result.Members.Single();
        alice.Points.Should().Be(5);
        alice.Completions.Should().Be(1);
    }

    [Fact]
    public void Compute_Week_TzBoundary_SundayLocalChicago_ExcludedFromCurrentWeek()
    {
        // Evaluate at 2026-06-17 12:00 UTC (Wednesday). CDT = UTC-5.
        //   Local Monday in Chicago = 2026-06-15 00:00 CDT = 2026-06-15 05:00 UTC.
        //   A completion at 2026-06-15 03:00 UTC is 2026-06-14 22:00 CDT (Sunday) → excluded.
        //   A completion at 2026-06-15 06:00 UTC is 2026-06-15 01:00 CDT (Monday) → included.
        var now = Utc0(2026, 6, 17, 12);

        var localSundayCompletion = Completion(userId: 1, Utc0(2026, 6, 15, 3), effortPoints: 2);
        var localMondayCompletion = Completion(userId: 1, Utc0(2026, 6, 15, 6), effortPoints: 4);
        var members = new[] { Member(1, "Alice") };

        var result = _calc.Compute(
            new[] { localSundayCompletion, localMondayCompletion },
            members,
            EquityWindow.Week,
            now,
            Chicago);

        result.TotalPoints.Should().Be(4, "the 03:00 UTC completion is local-Sunday in Chicago, outside the week window");
        result.TotalCompletions.Should().Be(1);
    }

    // ---- All window (V1) ----------------------------------------------------------------------------

    [Fact]
    public void Compute_All_IncludesCompletionsAcrossAllTime()
    {
        // All window: a completion from months ago must still be counted.
        var now = Utc0(2026, 6, 17, 12);
        var completions = new[]
        {
            Completion(userId: 1, Utc0(2025, 1, 1, 0), effortPoints: 3),
            Completion(userId: 1, Utc0(2026, 6, 15, 10), effortPoints: 2),
        };
        var members = new[] { Member(1, "Alice") };

        var result = _calc.Compute(completions, members, EquityWindow.All, now, Utc);

        result.TotalPoints.Should().Be(5);
        result.TotalCompletions.Should().Be(2);
        result.Members.Single().Points.Should().Be(5);
        result.Members.Single().SharePct.Should().Be(100.0);
    }

    // ---- Zero-completion member appears (V1) --------------------------------------------------------

    [Fact]
    public void Compute_MemberWithZeroCompletions_AppearsWithZeroValues()
    {
        var now = Utc0(2026, 6, 15, 12);
        var completions = new[]
        {
            Completion(userId: 1, Utc0(2026, 6, 15, 10), effortPoints: 4),
        };
        // Bob has no completions in the window.
        var members = new[] { Member(1, "Alice"), Member(2, "Bob") };

        var result = _calc.Compute(completions, members, EquityWindow.Week, now, Utc);

        result.Members.Should().HaveCount(2);
        var bob = result.Members.Single(m => m.UserId == 2);
        bob.Points.Should().Be(0);
        bob.Completions.Should().Be(0);
        bob.SharePct.Should().Be(0.0);
    }

    // ---- Empty household — no divide-by-zero (V1) ---------------------------------------------------

    [Fact]
    public void Compute_EmptyMemberList_ReturnsZeroResultWithoutException()
    {
        var now = Utc0(2026, 6, 15, 12);
        var completions = Array.Empty<ChoreCompletion>();
        var members = Array.Empty<MemberDto>();

        var act = () => _calc.Compute(completions, members, EquityWindow.Week, now, Utc);

        act.Should().NotThrow();
        var result = act();
        result.TotalPoints.Should().Be(0);
        result.TotalCompletions.Should().Be(0);
        result.EqualSharePct.Should().Be(0.0);
        result.Members.Should().BeEmpty();
    }

    [Fact]
    public void Compute_MembersButNoCompletions_AllSharePctZero_NoNaN()
    {
        var now = Utc0(2026, 6, 15, 12);
        var completions = Array.Empty<ChoreCompletion>();
        var members = new[] { Member(1, "Alice"), Member(2, "Bob"), Member(3, "Carol") };

        var result = _calc.Compute(completions, members, EquityWindow.Week, now, Utc);

        result.TotalPoints.Should().Be(0);
        result.EqualSharePct.Should().Be(33.3);
        result.Members.Should().AllSatisfy(m =>
        {
            m.SharePct.Should().Be(0.0);
            double.IsNaN(m.SharePct).Should().BeFalse("SharePct must never be NaN");
        });
    }

    // ---- SharePct is PERCENT 0..100 (V1) -----------------------------------------------------------

    [Fact]
    public void Compute_SharePctIsPercent_NotFraction()
    {
        // 3-member household: total 12 pts (Alice 5, Bob 4, Carol 3).
        // sharePct values must be 41.7 / 33.3 / 25 (percent), NOT 0.417 / 0.333 / 0.25 (fraction).
        var now = Utc0(2026, 6, 15, 12); // Monday
        var completions = new[]
        {
            Completion(userId: 1, Utc0(2026, 6, 15, 9), effortPoints: 3),
            Completion(userId: 1, Utc0(2026, 6, 15, 10), effortPoints: 2),
            Completion(userId: 2, Utc0(2026, 6, 15, 11), effortPoints: 4),
            Completion(userId: 3, Utc0(2026, 6, 15, 12), effortPoints: 3),
        };
        var members = new[]
        {
            Member(1, "Alice"),
            Member(2, "Bob"),
            Member(3, "Carol"),
        };

        var result = _calc.Compute(completions, members, EquityWindow.Week, now, Utc);

        result.TotalPoints.Should().Be(12);
        result.EqualSharePct.Should().Be(33.3); // 100/3 rounded to 1dp

        var alice = result.Members.Single(m => m.UserId == 1);
        alice.SharePct.Should().Be(41.7, "5/12 * 100 = 41.666... → 41.7");

        var bob = result.Members.Single(m => m.UserId == 2);
        bob.SharePct.Should().Be(33.3, "4/12 * 100 = 33.333... → 33.3");

        var carol = result.Members.Single(m => m.UserId == 3);
        carol.SharePct.Should().Be(25.0, "3/12 * 100 = 25.0 exactly");

        // All values are in the 0..100 range (not 0..1 fractions).
        result.Members.Should().AllSatisfy(m =>
            m.SharePct.Should().BeInRange(0.0, 100.0, "sharePct is PERCENT not fraction"));
    }

    // ---- Capacity weighting → ExpectedSharePct (Phase 15 WP-05, D3) --------------------------------

    private static IReadOnlyDictionary<int, string?> Tiers(params (int UserId, string? Tier)[] entries) =>
        entries.ToDictionary(e => e.UserId, e => e.Tier);

    [Fact]
    public void Compute_ExpectedSharePct_WorkedExample_FullFullMinimal()
    {
        // D3 worked example: members {Full, Full, Minimal} → weights {1.0, 1.0, 0.15}, Σ = 2.15.
        //   Alice: 100 * 1.0  / 2.15 ≈ 46.5
        //   Bob:   100 * 1.0  / 2.15 ≈ 46.5
        //   Carol: 100 * 0.15 / 2.15 ≈ 7.0
        // Unrounded doubles — assert ±0.1. Completions are irrelevant to ExpectedSharePct (it's
        // capacity-only), so use any.
        var now = Utc0(2026, 6, 15, 12);
        var completions = new[]
        {
            Completion(userId: 1, Utc0(2026, 6, 15, 10), effortPoints: 4),
        };
        var members = new[] { Member(1, "Alice"), Member(2, "Bob"), Member(3, "Carol") };
        var tiers = Tiers((1, "Full"), (2, "Full"), (3, "Minimal"));

        var result = _calc.Compute(completions, members, EquityWindow.Week, now, Utc, tiers);

        var alice = result.Members.Single(m => m.UserId == 1);
        var bob = result.Members.Single(m => m.UserId == 2);
        var carol = result.Members.Single(m => m.UserId == 3);

        alice.ExpectedSharePct.Should().BeApproximately(46.5, 0.1);
        bob.ExpectedSharePct.Should().BeApproximately(46.5, 0.1);
        carol.ExpectedSharePct.Should().BeApproximately(7.0, 0.1);

        // ExpectedSharePct is a closed distribution — sums to ~100.
        result.Members.Sum(m => m.ExpectedSharePct).Should().BeApproximately(100.0, 0.01);
    }

    [Fact]
    public void Compute_ExpectedSharePct_AbsentTierMap_FallsBackToFlatDistribution()
    {
        // No tiers passed (the digest call site path) ⇒ every member Full ⇒ flat 100/N each.
        var now = Utc0(2026, 6, 15, 12);
        var completions = new[]
        {
            Completion(userId: 1, Utc0(2026, 6, 15, 10), effortPoints: 5),
            Completion(userId: 2, Utc0(2026, 6, 15, 11), effortPoints: 1),
        };
        var members = new[] { Member(1, "Alice"), Member(2, "Bob"), Member(3, "Carol") };

        var result = _calc.Compute(completions, members, EquityWindow.Week, now, Utc);

        result.Members.Should().AllSatisfy(m =>
            m.ExpectedSharePct.Should().BeApproximately(100.0 / 3.0, 0.0001,
                "absent tier map ⇒ all Full ⇒ flat distribution (mirrors EqualSharePct's even split)"));
    }

    [Fact]
    public void Compute_ExpectedSharePct_NullAndUnrecognizedTiers_TreatedAsFull()
    {
        // A null tier and an unrecognized string both weight as Full (1.0). With one explicit Full,
        // all three are Full ⇒ flat distribution.
        var now = Utc0(2026, 6, 15, 12);
        var members = new[] { Member(1, "Alice"), Member(2, "Bob"), Member(3, "Carol") };
        var tiers = Tiers((1, "Full"), (2, null), (3, "bogus-tier"));

        var result = _calc.Compute(Array.Empty<ChoreCompletion>(), members, EquityWindow.Week, now, Utc, tiers);

        result.Members.Should().AllSatisfy(m =>
            m.ExpectedSharePct.Should().BeApproximately(100.0 / 3.0, 0.0001));
    }

    [Fact]
    public void Compute_ExpectedSharePct_AllMinimal_NoDivideByZero_EqualDistribution()
    {
        // All-Minimal household: weights {0.15, 0.15} → Σ = 0.30 > 0, normalizes to equal 50/50.
        // No divide-by-zero, no NaN (the D3 Σ==0 guard is belt-and-suspenders since Minimal=0.15>0).
        var now = Utc0(2026, 6, 15, 12);
        var members = new[] { Member(1, "Alice"), Member(2, "Bob") };
        var tiers = Tiers((1, "Minimal"), (2, "Minimal"));

        var result = _calc.Compute(Array.Empty<ChoreCompletion>(), members, EquityWindow.Week, now, Utc, tiers);

        result.Members.Should().AllSatisfy(m =>
        {
            m.ExpectedSharePct.Should().BeApproximately(50.0, 0.0001);
            double.IsNaN(m.ExpectedSharePct).Should().BeFalse("ExpectedSharePct must never be NaN");
        });
    }

    [Fact]
    public void Compute_ExpectedSharePct_EmptyHousehold_NoThrow()
    {
        // Empty member list with a (non-empty but irrelevant) tier map ⇒ no members, no throw, no NaN.
        var now = Utc0(2026, 6, 15, 12);
        var tiers = Tiers((99, "Minimal"));

        var act = () => _calc.Compute(
            Array.Empty<ChoreCompletion>(), Array.Empty<MemberDto>(), EquityWindow.Week, now, Utc, tiers);

        act.Should().NotThrow();
        act().Members.Should().BeEmpty();
    }

    [Fact]
    public void Compute_AddingTierMap_DoesNotChangeRawSharePctOrEqualSharePct()
    {
        // V1 invariant: capacity weighting touches ONLY ExpectedSharePct. SharePct / EqualSharePct are
        // byte-identical whether or not a tier map is supplied (D1 — raw shares stay raw).
        var now = Utc0(2026, 6, 15, 12);
        var completions = new[]
        {
            Completion(userId: 1, Utc0(2026, 6, 15, 9), effortPoints: 3),
            Completion(userId: 1, Utc0(2026, 6, 15, 10), effortPoints: 2),
            Completion(userId: 2, Utc0(2026, 6, 15, 11), effortPoints: 4),
            Completion(userId: 3, Utc0(2026, 6, 15, 12), effortPoints: 3),
        };
        var members = new[] { Member(1, "Alice"), Member(2, "Bob"), Member(3, "Carol") };
        var tiers = Tiers((1, "Full"), (2, "Reduced"), (3, "Minimal"));

        var withoutTiers = _calc.Compute(completions, members, EquityWindow.Week, now, Utc);
        var withTiers = _calc.Compute(completions, members, EquityWindow.Week, now, Utc, tiers);

        withTiers.EqualSharePct.Should().Be(withoutTiers.EqualSharePct);
        foreach (var m in withTiers.Members)
        {
            var baseline = withoutTiers.Members.Single(b => b.UserId == m.UserId);
            m.SharePct.Should().Be(baseline.SharePct, "capacity weighting must not change the RAW share (D1)");
        }
        // But ExpectedSharePct DID change away from flat for the non-Full members.
        withTiers.Members.Single(m => m.UserId == 3).ExpectedSharePct
            .Should().BeLessThan(100.0 / 3.0, "a Minimal member's expected reference is below the even split");
    }

    // ---- Determinism — no DateTime.UtcNow inside (V1) -----------------------------------------------

    [Fact]
    public void Compute_FrozenNow_ProducesIdenticalResultsOnRepeatedCalls()
    {
        var frozenNow = Utc0(2026, 6, 15, 12);
        var completions = new[] { Completion(userId: 1, Utc0(2026, 6, 15, 10), effortPoints: 2) };
        var members = new[] { Member(1, "Alice") };

        var r1 = _calc.Compute(completions, members, EquityWindow.Week, frozenNow, Utc);
        var r2 = _calc.Compute(completions, members, EquityWindow.Week, frozenNow, Utc);

        r1.Should().BeEquivalentTo(r2,
            "the calculator is pure and deterministic — injected now governs all date math");
    }
}
