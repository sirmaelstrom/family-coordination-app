using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Digest;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Tests for <see cref="ChoreHistoryService"/> — the shared history-aggregation service (Phase 15 WP-01).
/// Every test injects an explicit <c>now</c> (UTC) + <see cref="TimeZoneInfo"/> so the window/bucketing math is
/// deterministic. The load-bearing invariants pinned here: the weekly aggregates match the public recap
/// <c>Trend</c> for the same completions (reuse of <see cref="ChoreEquityCalculator.WeekStartUtc"/>), the
/// per-week distribution sums to the week total (displayName-only), first-ever is all-time (a pre-window
/// completion is NOT a first-ever — the bounded MIN semantics), the window lower bound is honored behaviorally,
/// and a second household's data never leaks (M1). Gap projection (Ghosts/GoneQuiet) is WP-02 — empty here.
/// </summary>
public class ChoreHistoryServiceTests
{
    private const int H1 = 1;
    private const int H2 = 2;
    private const int Alice = 100;
    private const int Bob = 101;
    private const int Carol = 200; // household 2

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

    public ChoreHistoryServiceTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var ctx = new ApplicationDbContext(_options);
        ctx.Households.AddRange(
            new Household { Id = H1, Name = "Smith" },
            new Household { Id = H2, Name = "Jones" });
        ctx.Users.AddRange(
            new User { Id = Alice, HouseholdId = H1, Email = "a@x.com", DisplayName = "Alice", Initials = "AL" },
            new User { Id = Bob, HouseholdId = H1, Email = "b@x.com", DisplayName = "Bob", Initials = "BO" },
            new User { Id = Carol, HouseholdId = H2, Email = "c@x.com", DisplayName = "Carol", Initials = "CA" });
        ctx.SaveChanges();
    }

    private ChoreHistoryService CreateService(TimeZoneInfo tz)
    {
        var dbFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        return new ChoreHistoryService(
            dbFactory.Object,
            new ChoreEquityCalculator(),
            new ChoreStatusCalculator(),
            tz,
            TimeProvider.System);
    }

    private ChoreRecapService CreateRecapService(TimeZoneInfo tz)
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

    private void Seed(Action<ApplicationDbContext> seed)
    {
        using var ctx = new ApplicationDbContext(_options);
        seed(ctx);
        ctx.SaveChanges();
    }

    private static ChoreCompletion Completion(
        int id, int userId, DateTime at, int pts, int choreId = 1, int householdId = H1,
        string? note = null, string? photo = null) =>
        new()
        {
            HouseholdId = householdId,
            ChoreId = choreId,
            CompletionId = id,
            CompletedByUserId = userId,
            CompletedAt = at,
            EffortPointsSnapshot = pts,
            Note = note,
            PhotoPath = photo,
        };

    private static Chore SimpleChore(int id, string name, int householdId = H1, int? roomId = null) =>
        new()
        {
            HouseholdId = householdId,
            ChoreId = id,
            Name = name,
            RoomId = roomId,
            RecurrenceMode = RecurrenceMode.Flexible,
            IntervalDays = 7,
            EffortTier = EffortTier.Standard,
            EffortPoints = 2,
            Status = ChoreStatus.Active,
            EnteredByUserId = Alice,
            AssignmentKind = AssignmentKind.None,
            CreatedAt = Utc0(2026, 1, 1),
        };

    // ── (a) Weeks match the public recap Trend; empty weeks appear as zeros ──────────────────────────

    [Fact]
    public async Task Weeks_MatchRecapTrend_ForSameCompletions_IncludingEmptyWeeks()
    {
        // now = Wed 2026-06-17 (week Mon 06-15). weeks=3 ⇒ [06-01 empty], [06-08], [06-15 current].
        Seed(ctx =>
        {
            ctx.Chores.Add(SimpleChore(1, "Dishes"));
            ctx.ChoreCompletions.AddRange(
                Completion(1, Alice, Utc0(2026, 6, 16, 9), 3),   // week 06-15 (current)
                Completion(2, Alice, Utc0(2026, 6, 9, 9), 2),    // week 06-08
                Completion(3, Bob, Utc0(2026, 6, 11, 9), 4),     // week 06-08
                Completion(4, Bob, Utc0(2026, 5, 20, 9), 9));    // < window ⇒ excluded
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 3, now: Utc0(2026, 6, 17, 12));
        var recap = await CreateRecapService(Utc).GetRecapAsync(H1, weeks: 3, now: Utc0(2026, 6, 17, 12));

        history.Weeks.Should().HaveCount(3);
        recap.Trend.Should().HaveCount(3);

        // Element-wise: the history weekly aggregates must equal the recap trend totals (same WeekStartUtc key).
        for (var i = 0; i < 3; i++)
        {
            history.Weeks[i].WeekStartLocal.Should().Be(recap.Trend[i].WeekStartLocal);
            history.Weeks[i].Completions.Should().Be(recap.Trend[i].TotalCompletions);
            history.Weeks[i].Points.Should().Be(recap.Trend[i].TotalPoints);
        }

        // The oldest week is empty and still present with zero counts.
        history.Weeks[0].WeekStartLocal.Should().Be("2026-06-01");
        history.Weeks[0].Completions.Should().Be(0);
        history.Weeks[0].Points.Should().Be(0);

        history.Weeks[1].Completions.Should().Be(2);
        history.Weeks[1].Points.Should().Be(6);

        // Window bounds are the oldest Monday → today (MN9 ISO strings).
        history.WindowStartLocal.Should().Be("2026-06-01");
        history.WindowEndLocal.Should().Be("2026-06-17");
    }

    [Fact]
    public async Task Weeks_HalfOpenBoundary_MatchesRecap_Chicago()
    {
        // Chicago: Mon 06-15 00:00 local == 05:00 UTC. 04:59 UTC → prior week; 05:00 UTC → current week.
        Seed(ctx =>
        {
            ctx.Chores.Add(SimpleChore(1, "Trash"));
            ctx.ChoreCompletions.AddRange(
                Completion(1, Alice, Utc0(2026, 6, 15, 4, 59), 2),  // → week 06-08
                Completion(2, Bob, Utc0(2026, 6, 15, 5, 0), 5));    // → week 06-15 (current)
        });

        var history = await CreateService(Chicago).GetHistoryAsync(H1, weeks: 2, now: Utc0(2026, 6, 17, 12));

        history.Weeks.Should().HaveCount(2);
        history.Weeks[0].WeekStartLocal.Should().Be("2026-06-08");
        history.Weeks[0].Completions.Should().Be(1);
        history.Weeks[0].Points.Should().Be(2);
        history.Weeks[1].WeekStartLocal.Should().Be("2026-06-15");
        history.Weeks[1].Completions.Should().Be(1);
        history.Weeks[1].Points.Should().Be(5);
    }

    // ── (b) Per-week distribution sums to the week total, displayName-only ────────────────────────────

    [Fact]
    public async Task WeekDistribution_SumsToWeekPoints_AndIsDisplayNameOnly()
    {
        Seed(ctx =>
        {
            ctx.Chores.Add(SimpleChore(1, "Dishes"));
            ctx.ChoreCompletions.AddRange(
                Completion(1, Alice, Utc0(2026, 6, 16, 9), 3),
                Completion(2, Bob, Utc0(2026, 6, 16, 10), 1));
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 1, now: Utc0(2026, 6, 17, 12));

        var week = history.Weeks.Single();
        week.Points.Should().Be(4);
        week.Distribution.Sum(m => m.Points).Should().Be(week.Points,
            "each week's distribution must sum to that week's points (equity over the week bucket)");

        // Both members present, alphabetical, displayName-only (RecapMemberLineDto has no userId by shape).
        week.Distribution.Should().HaveCount(2);
        week.Distribution[0].DisplayName.Should().Be("Alice");
        week.Distribution[0].Points.Should().Be(3);
        week.Distribution[0].SharePct.Should().BeApproximately(75.0, 0.1);
        week.Distribution[1].DisplayName.Should().Be("Bob");
        week.Distribution[1].Points.Should().Be(1);
    }

    // ── (c) Milestones — first-ever is ALL-TIME; best week ranks by points ───────────────────────────

    [Fact]
    public async Task FirstEvers_ExcludePreWindowChore_IncludeInWindowFirst()
    {
        // Chore "Old" was first done in MARCH (before the 3-week window) → NOT a first-ever, even though it also
        // recurs in-window. Chore "New" is first ever done in-window → IS a first-ever.
        Seed(ctx =>
        {
            ctx.Chores.AddRange(SimpleChore(1, "Old"), SimpleChore(2, "New"));
            ctx.ChoreCompletions.AddRange(
                Completion(1, Alice, Utc0(2026, 3, 2, 9), 2, choreId: 1),    // Old — all-time MIN, pre-window
                Completion(2, Alice, Utc0(2026, 6, 16, 9), 2, choreId: 1),   // Old — in-window recurrence
                Completion(3, Bob, Utc0(2026, 6, 10, 9), 2, choreId: 2));    // New — all-time MIN, in-window
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 3, now: Utc0(2026, 6, 17, 12));

        history.Milestones.FirstEvers.Should().ContainSingle()
            .Which.ChoreName.Should().Be("New", "only a chore whose ALL-TIME first completion is in-window counts");
        history.Milestones.FirstEvers.Should().NotContain(f => f.ChoreName == "Old");
    }

    [Fact]
    public async Task BestWeek_PicksHigherPointWeek_StreakAndSeasonTotals()
    {
        // weeks=3 [06-01],[06-08],[06-15]. 06-08 = 6 pts / 2 comp; 06-15 = 3 pts / 1 comp; 06-01 empty.
        Seed(ctx =>
        {
            ctx.Chores.Add(SimpleChore(1, "Dishes"));
            ctx.ChoreCompletions.AddRange(
                Completion(1, Alice, Utc0(2026, 6, 9, 9), 2),
                Completion(2, Bob, Utc0(2026, 6, 11, 9), 4),
                Completion(3, Alice, Utc0(2026, 6, 16, 9), 3));
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 3, now: Utc0(2026, 6, 17, 12));

        history.Milestones.BestWeek.Should().NotBeNull();
        history.Milestones.BestWeek!.WeekStartLocal.Should().Be("2026-06-08", "it has the most points (6)");
        history.Milestones.BestWeek.Points.Should().Be(6);
        history.Milestones.BestWeek.Completions.Should().Be(2);

        // 06-08 and 06-15 are consecutive non-empty ⇒ streak 2 (the empty 06-01 breaks nothing before it).
        history.Milestones.LongestActiveStreakWeeks.Should().Be(2);

        // Season totals tile the window (sum of week buckets).
        history.Milestones.SeasonTotalCompletions.Should().Be(3);
        history.Milestones.SeasonTotalPoints.Should().Be(9);
    }

    [Fact]
    public async Task BestWeek_IsNull_WhenWindowHasNoCompletions()
    {
        Seed(ctx => ctx.Chores.Add(SimpleChore(1, "Dishes")));

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 4, now: Utc0(2026, 6, 17, 12));

        history.Milestones.BestWeek.Should().BeNull();
        history.Milestones.LongestActiveStreakWeeks.Should().Be(0);
        history.Milestones.SeasonTotalCompletions.Should().Be(0);
    }

    // ── (d) Window lower bound is honored behaviorally ───────────────────────────────────────────────

    [Fact]
    public async Task PreWindowCompletion_NeverAppearsInEventsOrWeeks()
    {
        Seed(ctx =>
        {
            ctx.Chores.Add(SimpleChore(1, "Dishes"));
            ctx.ChoreCompletions.AddRange(
                Completion(1, Alice, Utc0(2026, 6, 16, 9), 3),   // in-window
                Completion(2, Bob, Utc0(2026, 5, 1, 9), 5));     // well before oldestWeekStartUtc (06-01)
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 3, now: Utc0(2026, 6, 17, 12));

        history.Events.Should().ContainSingle("only the in-window completion surfaces");
        history.Events[0].ChoreName.Should().Be("Dishes");
        history.Weeks.Sum(w => w.Completions).Should().Be(1);
        history.Weeks.Sum(w => w.Points).Should().Be(3);
    }

    // ── (e) Kept moments cap; roomless → General; household isolation ────────────────────────────────

    [Fact]
    public async Task KeptMoments_CapAtTwelve_NewestFirst()
    {
        Seed(ctx =>
        {
            ctx.Chores.Add(SimpleChore(1, "Dishes"));
            // 15 note-carrying completions across the current week, ascending in time.
            for (var i = 0; i < 15; i++)
            {
                ctx.ChoreCompletions.Add(Completion(
                    i + 1, Alice, Utc0(2026, 6, 15, 6).AddMinutes(i), 1, note: $"note {i}"));
            }
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 1, now: Utc0(2026, 6, 17, 12));

        history.KeptMoments.Should().HaveCount(12, "the reel is capped at 12");
        history.KeptMoments[0].Note.Should().Be("note 14", "newest first");
        history.KeptMoments[11].Note.Should().Be("note 3");
    }

    [Fact]
    public async Task KeptMoments_IncludePhotoOnlyCompletions()
    {
        Seed(ctx =>
        {
            ctx.Chores.Add(SimpleChore(1, "Dishes"));
            ctx.ChoreCompletions.AddRange(
                Completion(1, Alice, Utc0(2026, 6, 16, 9), 2, photo: "chores/p.jpg"),  // photo, no note
                Completion(2, Bob, Utc0(2026, 6, 16, 10), 2));                         // neither ⇒ not a moment
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 1, now: Utc0(2026, 6, 17, 12));

        history.KeptMoments.Should().ContainSingle();
        history.KeptMoments[0].HasPhoto.Should().BeTrue();
        history.KeptMoments[0].Note.Should().BeNull();
    }

    [Fact]
    public async Task WhatGotTended_TalliesByRoom_RoomlessUnderGeneral()
    {
        Seed(ctx =>
        {
            ctx.Rooms.Add(new Room { HouseholdId = H1, RoomId = 1, Name = "Kitchen" });
            ctx.Chores.AddRange(
                SimpleChore(1, "Dishes", roomId: 1),   // Kitchen
                SimpleChore(2, "Errand"));              // roomless ⇒ General
            ctx.ChoreCompletions.AddRange(
                Completion(1, Alice, Utc0(2026, 6, 16, 9), 2, choreId: 1),
                Completion(2, Bob, Utc0(2026, 6, 16, 10), 2, choreId: 1),
                Completion(3, Alice, Utc0(2026, 6, 16, 11), 2, choreId: 2));
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 1, now: Utc0(2026, 6, 17, 12));

        history.WhatGotTended.Should().HaveCount(2);
        history.WhatGotTended[0].RoomName.Should().Be("Kitchen", "most-tended first (2 completions)");
        history.WhatGotTended[0].Completions.Should().Be(2);
        history.WhatGotTended.Should().Contain(t => t.RoomName == "General" && t.Completions == 1);
    }

    [Fact]
    public async Task SecondHouseholdData_NeverAppears()
    {
        Seed(ctx =>
        {
            ctx.Chores.AddRange(
                SimpleChore(1, "Smith dishes", householdId: H1),
                SimpleChore(1, "Jones secret", householdId: H2));
            ctx.ChoreCompletions.AddRange(
                Completion(1, Alice, Utc0(2026, 6, 16, 9), 3, choreId: 1, householdId: H1),
                Completion(2, Carol, Utc0(2026, 6, 16, 10), 99, choreId: 1, householdId: H2));
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 1, now: Utc0(2026, 6, 17, 12));

        history.Events.Should().ContainSingle().Which.ChoreName.Should().Be("Smith dishes");
        history.Events.Should().NotContain(e => e.DoerDisplayName == "Carol");
        history.Milestones.SeasonTotalPoints.Should().Be(3, "household 2's 99 points must not leak");
    }

    // ── Gap projection is WP-02 — empty here ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GhostsAndGoneQuiet_AreEmpty_InWp01()
    {
        Seed(ctx =>
        {
            ctx.Chores.Add(SimpleChore(1, "Dishes"));
            ctx.ChoreCompletions.Add(Completion(1, Alice, Utc0(2026, 6, 16, 9), 3));
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 3, now: Utc0(2026, 6, 17, 12));

        history.Ghosts.Should().BeEmpty();
        history.GoneQuiet.Should().BeEmpty();
    }
}
