using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Digest;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Tests for <see cref="ChoreRecapService"/> + the extracted <see cref="ChoreEquityCalculator.WeekStartUtc"/>
/// helper. Every test injects an explicit <c>now</c> (UTC) and an explicit <see cref="TimeZoneInfo"/>, so the
/// week-boundary math is deterministic. The trend-bucketing tests are the load-bearing ones: they pin the
/// half-open <c>[weekStart, weekEnd)</c> windowing across the household-local Monday-start boundary (a
/// Sunday-23:59 completion and the next Monday-00:00 completion must land in different weeks).
/// </summary>
public class ChoreRecapServiceTests
{
    private const int H1 = 1;
    private const int Alice = 100;
    private const int Bob = 101;

    // America/Chicago is UTC−5 in June (CDT), so local-Monday-00:00 is 05:00 UTC — local and UTC week
    // boundaries fall on different instants, which is exactly what we want to test.
    private static readonly TimeZoneInfo Chicago = ResolveTz("America/Chicago", "Central Standard Time");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static TimeZoneInfo ResolveTz(string ianaId, string windowsId)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
    }

    private static DateTime Utc0(int y, int m, int d, int h = 0, int min = 0) =>
        new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<ApplicationDbContext> _options;

    public ChoreRecapServiceTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var ctx = new ApplicationDbContext(_options);
        ctx.Households.Add(new Household { Id = H1, Name = "Smith" });
        ctx.Users.AddRange(
            new User { Id = Alice, HouseholdId = H1, Email = "a@x.com", DisplayName = "Alice", Initials = "AL" },
            new User { Id = Bob, HouseholdId = H1, Email = "b@x.com", DisplayName = "Bob", Initials = "BO" });
        ctx.SaveChanges();
    }

    private ChoreRecapService CreateService(TimeZoneInfo tz)
    {
        var dbFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        return new ChoreRecapService(
            dbFactory.Object,
            new ChoreEquityCalculator(),
            new ChoreStatusCalculator(),
            new DigestBuilder(),
            tz,
            TimeProvider.System);
    }

    private void SeedCompletions(params ChoreCompletion[] completions)
    {
        using var ctx = new ApplicationDbContext(_options);
        ctx.ChoreCompletions.AddRange(completions);
        ctx.SaveChanges();
    }

    private static ChoreCompletion Completion(int id, int userId, DateTime completedAtUtc, int effortPoints) =>
        new()
        {
            HouseholdId = H1,
            ChoreId = 1,
            CompletionId = id,
            CompletedByUserId = userId,
            CompletedAt = completedAtUtc,
            EffortPointsSnapshot = effortPoints,
        };

    // ── WeekStartUtc (pure boundary math) ────────────────────────────────────────────

    [Fact]
    public void WeekStartUtc_Utc_SundayBelongsToPriorMonday()
    {
        // Sunday 2026-06-21 is in the week that STARTED Monday 2026-06-15.
        ChoreEquityCalculator.WeekStartUtc(Utc0(2026, 6, 21, 12), Utc)
            .Should().Be(Utc0(2026, 6, 15));
    }

    [Fact]
    public void WeekStartUtc_Utc_MondayIsItsOwnWeekStart()
    {
        ChoreEquityCalculator.WeekStartUtc(Utc0(2026, 6, 15, 0), Utc)
            .Should().Be(Utc0(2026, 6, 15));
    }

    [Fact]
    public void WeekStartUtc_Chicago_LocalMondayMidnightIsFiveAmUtc()
    {
        // Mon 2026-06-15 00:00 CDT == 05:00 UTC. A "now" of Mon 00:00 local resolves to that instant.
        ChoreEquityCalculator.WeekStartUtc(Utc0(2026, 6, 15, 5), Chicago)
            .Should().Be(Utc0(2026, 6, 15, 5));
    }

    [Fact]
    public void WeekStartUtc_Chicago_SundayLateLocalStillPriorWeek()
    {
        // Sun 2026-06-14 23:59 CDT == 06-15 04:59 UTC — still the week that started Mon 06-08 (05:00 UTC).
        ChoreEquityCalculator.WeekStartUtc(Utc0(2026, 6, 15, 4, 59), Chicago)
            .Should().Be(Utc0(2026, 6, 8, 5));
    }

    // ── Trend bucketing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Trend_BucketsCompletionsByWeek_OldestToNewest_CurrentLast()
    {
        // now = Wed 2026-06-17 (week of Mon 06-15). weeks=3 ⇒ [06-01), [06-08), [06-15 current].
        SeedCompletions(
            Completion(1, Alice, Utc0(2026, 6, 16, 9), 3),   // week2 (current, Mon 06-15)
            Completion(2, Alice, Utc0(2026, 6, 9, 9), 2),    // week1 (Mon 06-08)
            Completion(3, Bob, Utc0(2026, 6, 11, 9), 4),     // week1 (Mon 06-08)
            Completion(4, Bob, Utc0(2026, 5, 20, 9), 9));    // older than the 3-week window — excluded

        var svc = CreateService(Utc);
        var recap = await svc.GetRecapAsync(H1, weeks: 3, now: Utc0(2026, 6, 17, 12));

        recap.Trend.Should().HaveCount(3);

        recap.Trend[0].WeekStartLocal.Should().Be("2026-06-01");
        recap.Trend[0].TotalCompletions.Should().Be(0);
        recap.Trend[0].TotalPoints.Should().Be(0);
        recap.Trend[0].IsCurrent.Should().BeFalse();

        recap.Trend[1].WeekStartLocal.Should().Be("2026-06-08");
        recap.Trend[1].TotalCompletions.Should().Be(2);
        recap.Trend[1].TotalPoints.Should().Be(6);
        recap.Trend[1].IsCurrent.Should().BeFalse();

        recap.Trend[2].WeekStartLocal.Should().Be("2026-06-15");
        recap.Trend[2].TotalCompletions.Should().Be(1);
        recap.Trend[2].TotalPoints.Should().Be(3);
        recap.Trend[2].IsCurrent.Should().BeTrue();

        // Current week matches the last trend point (same lower bound as the equity Week path).
        recap.Current.TotalCompletions.Should().Be(1);
        recap.Current.TotalPoints.Should().Be(3);
        recap.Current.WeekStartLocal.Should().Be("2026-06-15");
    }

    [Fact]
    public async Task Trend_HalfOpenBoundary_SundayNightAndMondayMidnightSplitAcrossWeeks_Chicago()
    {
        // The crux: in Chicago, Mon 06-15 00:00 local == 05:00 UTC.
        //   06-15 04:59 UTC == Sun 23:59 CDT  → belongs to the PRIOR week (Mon 06-08)
        //   06-15 05:00 UTC == Mon 00:00 CDT  → belongs to the CURRENT week (Mon 06-15)
        SeedCompletions(
            Completion(1, Alice, Utc0(2026, 6, 15, 4, 59), 2),  // → week Mon 06-08
            Completion(2, Bob, Utc0(2026, 6, 15, 5, 0), 5));    // → week Mon 06-15 (current)

        var svc = CreateService(Chicago);
        var recap = await svc.GetRecapAsync(H1, weeks: 2, now: Utc0(2026, 6, 17, 12));

        recap.Trend.Should().HaveCount(2);

        recap.Trend[0].WeekStartLocal.Should().Be("2026-06-08");
        recap.Trend[0].TotalCompletions.Should().Be(1);
        recap.Trend[0].TotalPoints.Should().Be(2);

        recap.Trend[1].WeekStartLocal.Should().Be("2026-06-15");
        recap.Trend[1].TotalCompletions.Should().Be(1);
        recap.Trend[1].TotalPoints.Should().Be(5);
        recap.Trend[1].IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task Current_MirrorsDigestHeadlineAndDistribution()
    {
        // Two members both active in the current week (Mon 06-15): Alice 3pts, Bob 1pt ⇒ 4 total, 2 completions.
        SeedCompletions(
            Completion(1, Alice, Utc0(2026, 6, 16, 9), 3),
            Completion(2, Bob, Utc0(2026, 6, 16, 10), 1));

        var svc = CreateService(Utc);
        var recap = await svc.GetRecapAsync(H1, weeks: 4, now: Utc0(2026, 6, 17, 12));

        recap.Current.TotalCompletions.Should().Be(2);
        recap.Current.TotalPoints.Should().Be(4);
        // DigestBuilder headline format (collective, non-punitive) — proves we reuse the same builder.
        recap.Current.Headline.Should().Contain("Smith").And.Contain("2 chores").And.Contain("4 pts");
        // Distribution is neutral alphabetical, both members present, shares are percent 0–100.
        recap.Current.Distribution.Should().HaveCount(2);
        recap.Current.Distribution[0].DisplayName.Should().Be("Alice");
        recap.Current.Distribution[0].Points.Should().Be(3);
        recap.Current.Distribution[0].SharePct.Should().BeApproximately(75.0, 0.1);
        recap.Current.Distribution[1].DisplayName.Should().Be("Bob");
        recap.Current.Distribution[1].SharePct.Should().BeApproximately(25.0, 0.1);
    }

    [Fact]
    public async Task Weeks_IsClampedToOneThroughTwentySix()
    {
        var svc = CreateService(Utc);

        (await svc.GetRecapAsync(H1, weeks: 0, now: Utc0(2026, 6, 17, 12))).Trend.Should().HaveCount(1);
        (await svc.GetRecapAsync(H1, weeks: 999, now: Utc0(2026, 6, 17, 12))).Trend.Should().HaveCount(26);
    }
}
