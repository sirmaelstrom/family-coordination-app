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
            tz,
            TimeProvider.System);
    }

    private ChoreRecapService CreateRecapService(TimeZoneInfo tz)
    {
        var dbFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        var equity = new ChoreEquityCalculator();
        var history = new ChoreHistoryService(dbFactory.Object, equity, tz, TimeProvider.System);

        return new ChoreRecapService(
            dbFactory.Object,
            equity,
            new ChoreStatusCalculator(),
            new DigestBuilder(),
            tz,
            TimeProvider.System,
            history);
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

    private static Chore SimpleChore(int id, string name, int householdId = H1) =>
        new()
        {
            HouseholdId = householdId,
            ChoreId = id,
            Name = name,
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
                SimpleChore(1, "Dishes"),   // Kitchen (via the membership row below)
                SimpleChore(2, "Errand"));  // roomless ⇒ General
            // Phase 13: the tally now reads ChoreRoom memberships, not Chore.RoomId — seed the membership row.
            ctx.ChoreRooms.Add(new ChoreRoom { HouseholdId = H1, ChoreId = 1, RoomId = 1 });
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
    public async Task WhatGotTended_MultiRoomCompletion_CountsInEachMemberRoom()
    {
        // Phase 13 / D6: a completion of a chore in {Kitchen, Bathroom} counts toward BOTH rooms; a roomless
        // completion → General. The per-room tally therefore sums to more than the distinct completion count.
        Seed(ctx =>
        {
            ctx.Rooms.AddRange(
                new Room { HouseholdId = H1, RoomId = 1, Name = "Kitchen" },
                new Room { HouseholdId = H1, RoomId = 2, Name = "Bathroom" });
            ctx.Chores.AddRange(
                SimpleChore(1, "Deep clean"),   // member of BOTH rooms (via the membership rows below)
                SimpleChore(2, "Errand"));      // roomless ⇒ General
            ctx.ChoreRooms.AddRange(
                new ChoreRoom { HouseholdId = H1, ChoreId = 1, RoomId = 1 },
                new ChoreRoom { HouseholdId = H1, ChoreId = 1, RoomId = 2 });
            ctx.ChoreCompletions.AddRange(
                Completion(1, Alice, Utc0(2026, 6, 16, 9), 2, choreId: 1),    // one completion of the 2-room chore
                Completion(2, Bob, Utc0(2026, 6, 16, 11), 2, choreId: 2));    // roomless completion
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 1, now: Utc0(2026, 6, 17, 12));

        // 2 distinct completions → 3 room-tally increments (Kitchen+Bathroom from the one multi-room completion).
        history.WhatGotTended.Should().Contain(t => t.RoomName == "Kitchen" && t.Completions == 1);
        history.WhatGotTended.Should().Contain(t => t.RoomName == "Bathroom" && t.Completions == 1);
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

    // ═══════════════════════════════════════════════════════════════════════════════════════════════
    // WP-02 — historical gap/ghost projection. The DST-heavy scenarios (S1/S2/S4/S6/S7/S-tol) test the
    // pure ProjectBeats/Match functions directly (mirroring the validated spike); S-GQ + reason + monthly
    // exercise the GetHistoryAsync integration. now = 2026-11-15 18:00 UTC (window crosses BOTH 2026 DST
    // transitions: spring-fwd 2026-03-08, fall-back 2026-11-01).
    // ═══════════════════════════════════════════════════════════════════════════════════════════════

    // A completion stamped at local `hour` in Chicago, stored as the UTC instant (mirrors the spike's DoneAt).
    private static DateTime DoneAtUtc(int y, int m, int d, int localHour = 10) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, localHour, 0, 0, DateTimeKind.Unspecified), Chicago);

    private static readonly DateOnly S1Today = ChoreStatusCalculator.LocalDate(Utc0(2026, 11, 15, 18), Chicago);

    // ── S1 — snoozed-since-March Fixed weekly Mon+Thu: 72 missed, DST beats present + missed ──────────

    [Fact]
    public void S1_FixedWeekly_SnoozedSinceMarch_72MissedBeats_AcrossBothDst()
    {
        var winStart = new DateOnly(2026, 2, 2); // Monday — the chore's history start
        var snap = new ChoreRecurrenceSnapshot(
            RecurrenceMode.Fixed, IntervalDays: null, AnchorDate: null,
            DaysOfWeek: ChoreDaysOfWeek.Monday | ChoreDaysOfWeek.Thursday, DayOfMonth: null,
            LastCompletedAt: DoneAtUtc(2026, 3, 5), SnoozedUntil: new DateOnly(2026, 12, 31));

        var beats = ChoreHistoryService.ProjectBeats(snap, winStart, S1Today, Chicago);

        // Completed regularly Feb..Mar 5 (the 10 Mon/Thu completions), then nothing.
        var done = new List<DateOnly>
        {
            new(2026, 2, 2), new(2026, 2, 5), new(2026, 2, 9), new(2026, 2, 12),
            new(2026, 2, 16), new(2026, 2, 19), new(2026, 2, 23), new(2026, 2, 26),
            new(2026, 3, 2), new(2026, 3, 5),
        };
        var (kept, missed) = ChoreHistoryService.Match(beats, done, backTol: 1, fwdTol: 3);

        kept.Should().HaveCount(10, "the 10 Feb..Mar completions each keep their beat, none mis-counted");
        missed.Should().HaveCount(72, "every Mon/Thu after Mar 5 through today is a surfaced miss");

        // DST spot-checks: the Monday AFTER spring-forward and the Monday AFTER fall-back are both real,
        // correctly-dated beats and both missed (UTC-tick arithmetic would drift these off Monday — M4/MN4).
        beats.Should().Contain(new DateOnly(2026, 3, 9));
        missed.Should().Contain(new DateOnly(2026, 3, 9));
        beats.Should().Contain(new DateOnly(2026, 11, 2));
        missed.Should().Contain(new DateOnly(2026, 11, 2));
    }

    // ── S2 — kept-current control: ZERO false positives across both DST transitions ──────────────────

    [Fact]
    public void S2_FixedWeeklySunday_CompletedEverySunday_ZeroGhosts()
    {
        var winStart = new DateOnly(2026, 2, 1); // Sunday
        var snap = new ChoreRecurrenceSnapshot(
            RecurrenceMode.Fixed, null, null, ChoreDaysOfWeek.Sunday, null, LastCompletedAt: null);

        var beats = ChoreHistoryService.ProjectBeats(snap, winStart, S1Today, Chicago);

        var done = new List<DateOnly>();
        for (var d = winStart; d <= S1Today; d = d.AddDays(7))
        {
            done.Add(d);
        }
        var (kept, missed) = ChoreHistoryService.Match(beats, done, 1, 3);

        missed.Should().BeEmpty("a kept-current chore across BOTH DST transitions has NO false-positive ghosts");
        kept.Should().HaveCount(beats.Count, "every Sunday beat matched exactly one completion");
    }

    // ── S4 — Flexible: trailing-only; interior late gap is not a missed beat ─────────────────────────

    [Fact]
    public void S4_Flexible_ProjectsFromLastCompletion_InteriorLateGapNotMissed()
    {
        var winStart = new DateOnly(2026, 6, 1);
        var snap = new ChoreRecurrenceSnapshot(
            RecurrenceMode.Flexible, IntervalDays: 7, AnchorDate: null, DaysOfWeek: null, DayOfMonth: null,
            LastCompletedAt: DoneAtUtc(2026, 6, 25)); // done Jun 1 & Jun 25 (late); Jun 25 reset the clock

        var beats = ChoreHistoryService.ProjectBeats(snap, winStart, S1Today, Chicago);

        beats.Should().NotBeEmpty();
        beats[0].Should().Be(new DateOnly(2026, 7, 2), "flexible projects from LAST completion (Jun 25 + 7), not Jun 8");
        beats.Should().NotContain(new DateOnly(2026, 6, 8), "the interior late gap is a late completion, not a missed beat");

        var done = new List<DateOnly> { new(2026, 6, 1), new(2026, 6, 25) };
        var (_, missed) = ChoreHistoryService.Match(beats, done, 1, 3);
        missed.Should().HaveCount(beats.Count, "all trailing beats after Jun 25 are missed");
    }

    // ── S6 — no-collapse: 3 missed Wednesdays + ONE Done → 2 missed + 1 kept (not 0) ─────────────────

    [Fact]
    public void S6_FixedWeekly_OneCompletionDoesNotCollapseBacklog()
    {
        var winStart = new DateOnly(2026, 6, 3); // Wednesday
        var today = new DateOnly(2026, 6, 24);
        var snap = new ChoreRecurrenceSnapshot(
            RecurrenceMode.Fixed, null, null, ChoreDaysOfWeek.Wednesday, null, LastCompletedAt: DoneAtUtc(2026, 6, 24));

        var beats = ChoreHistoryService.ProjectBeats(snap, winStart, today, Chicago); // Jun 3,10,17,24
        var (kept, missed) = ChoreHistoryService.Match(beats, new List<DateOnly> { new(2026, 6, 24) }, 1, 3);

        kept.Should().HaveCount(1, "one completion clears exactly one beat — history does NOT collapse like the board");
        missed.Should().HaveCount(beats.Count - 1, "the earlier missed Wednesdays stay missed (honest history)");
    }

    // ── monthly (DayOfMonth) — OUT OF SCOPE: zero beats generated ────────────────────────────────────

    [Fact]
    public void Monthly_DayOfMonthChore_GeneratesNoBeats()
    {
        var snap = new ChoreRecurrenceSnapshot(
            RecurrenceMode.Fixed, IntervalDays: null, AnchorDate: null, DaysOfWeek: null,
            DayOfMonth: 15, LastCompletedAt: null);

        var beats = ChoreHistoryService.ProjectBeats(snap, new DateOnly(2026, 1, 1), S1Today, Chicago);

        beats.Should().BeEmpty("monthly-on-day recurrence is rejected in v1 — never project it (MN5)");
    }

    // ── naive-UTC control — documents why DateOnly generation is mandatory across the fall-back ──────

    [Fact]
    public void NaiveUtcTickStepping_DriftsAcrossFallBack_WhileDateOnlyStaysOnMonday()
    {
        var firstMon = new DateOnly(2026, 10, 19);
        var correct = new List<DateOnly>();
        for (var d = firstMon; d <= new DateOnly(2026, 11, 16); d = d.AddDays(7))
        {
            correct.Add(d);
        }

        // Naive: start at local-midnight-UTC, step fixed 7×24h ticks, convert back to local date.
        var naive = new List<DateOnly>();
        var utc = ChoreStatusCalculator.LocalMidnightUtc(firstMon, Chicago);
        for (var i = 0; i < correct.Count; i++)
        {
            naive.Add(ChoreStatusCalculator.LocalDate(utc, Chicago));
            utc = utc.AddDays(7);
        }

        correct.Should().OnlyContain(d => d.DayOfWeek == DayOfWeek.Monday, "DateOnly stepping stays on Monday");
        naive.Should().Contain(d => d.DayOfWeek != DayOfWeek.Monday,
            "naive UTC-tick stepping DRIFTS off Monday after the fall-back — the false-ghost trap M4/MN4 forbid");
    }

    // ── S-tol (D8) — on-time keeps; fwd+1 late leaves the beat missed ────────────────────────────────

    [Fact]
    public void STol_OnTimeKeeps_ButOneBeyondForwardToleranceMisses()
    {
        var beats = new List<DateOnly> { new(2026, 6, 10) };

        // On-time (localDate == beat): kept.
        ChoreHistoryService.Match(beats, new List<DateOnly> { new(2026, 6, 10) }, 1, 3)
            .Kept.Should().ContainSingle();

        // fwd+1 days late (weekly fwd = 3 ⇒ 4 days after): the beat stays missed.
        ChoreHistoryService.Match(beats, new List<DateOnly> { new(2026, 6, 14) }, 1, 3)
            .Missed.Should().ContainSingle();
    }

    // ── S-GQ (D10) — gone-quiet detection through the GetHistoryAsync integration ─────────────────────

    // A Fixed weekly chore created before the window (so beats generate across it).
    private static Chore WeeklyChore(
        int id, string name, ChoreDaysOfWeek days, DateTime? lastCompletedAt = null, DateOnly? snoozedUntil = null) =>
        new()
        {
            HouseholdId = H1,
            ChoreId = id,
            Name = name,
            RecurrenceMode = RecurrenceMode.Fixed,
            DaysOfWeek = days,
            LastCompletedAt = lastCompletedAt,
            SnoozedUntil = snoozedUntil,
            EffortTier = EffortTier.Standard,
            EffortPoints = 2,
            Status = ChoreStatus.Active,
            EnteredByUserId = Alice,
            AssignmentKind = AssignmentKind.None,
            CreatedAt = Utc0(2026, 1, 1),
        };

    [Fact]
    public async Task GoneQuiet_NeverCompletedWeekly_AppearsWithCadenceAndNullLastCompleted()
    {
        // Fixed weekly Wed, never completed, created Jan. now = Wed 2026-06-24, weeks=4 ⇒ window Mon 06-01.
        // Beats Jun 3/10/17/24 all missed (never done) ⇒ 4 trailing misses ⇒ gone quiet.
        Seed(ctx => ctx.Chores.Add(WeeklyChore(1, "Water plants", ChoreDaysOfWeek.Wednesday)));

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 4, now: Utc0(2026, 6, 24, 12));

        history.GoneQuiet.Should().ContainSingle();
        var gq = history.GoneQuiet[0];
        gq.ChoreName.Should().Be("Water plants");
        gq.CadenceLabel.Should().Be("Wed");
        gq.LastCompletedLocalDate.Should().BeNull("a never-completed chore has no last-completed date");
        gq.Reason.Should().Be("slipped", "no snooze floor ⇒ slipped, not snoozed");

        history.Ghosts.Should().HaveCount(4, "every missed Wednesday in the window is a ghost");
        history.Ghosts.Should().OnlyContain(g => g.ChoreName == "Water plants" && g.ReasonFromLog == false);
    }

    [Fact]
    public async Task GoneQuiet_SingleTrailingMiss_DoesNotQualify()
    {
        // Completed on Jun 17 (keeps that beat); only Jun 24 is a trailing miss ⇒ 1 < threshold ⇒ NOT gone quiet.
        Seed(ctx =>
        {
            ctx.Chores.Add(WeeklyChore(1, "Vacuum", ChoreDaysOfWeek.Wednesday, lastCompletedAt: Utc0(2026, 6, 17, 12)));
            ctx.ChoreCompletions.Add(Completion(1, Alice, Utc0(2026, 6, 17, 12), 2));
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 4, now: Utc0(2026, 6, 24, 12));

        history.GoneQuiet.Should().BeEmpty("a single trailing miss is a blip, not silence");
        history.Ghosts.Should().Contain(g => g.ExpectedLocalDate == "2026-06-24", "the trailing miss is still a ghost");
    }

    [Fact]
    public async Task Snooze_DoesNotSuppressGeneration_GhostsReadSnoozedPlaceholder()
    {
        // A snoozed-to-December chore STILL generates its nominal beats (MN7) — snooze only labels the reason.
        // WP-02's reason is a current-state placeholder (ReasonFromLog=false); WP-03 overwrites from the log.
        Seed(ctx => ctx.Chores.Add(
            WeeklyChore(1, "Take out trash", ChoreDaysOfWeek.Wednesday, snoozedUntil: new DateOnly(2026, 12, 31))));

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 4, now: Utc0(2026, 6, 24, 12));

        history.Ghosts.Should().HaveCount(4, "snooze does NOT suppress beat generation (MN7)");
        history.Ghosts.Should().OnlyContain(g => g.Reason == "snoozed" && g.ReasonFromLog == false);
        history.GoneQuiet.Should().ContainSingle().Which.Reason.Should().Be("snoozed");
    }

    [Fact]
    public async Task Monthly_ChoreProducesNoGhosts_Integration()
    {
        // A DayOfMonth-only Fixed chore is skipped entirely by the projection (MN5) — no ghosts, no gone-quiet.
        Seed(ctx => ctx.Chores.Add(new Chore
        {
            HouseholdId = H1,
            ChoreId = 1,
            Name = "Pay rent",
            RecurrenceMode = RecurrenceMode.Fixed,
            DayOfMonth = 1,
            EffortTier = EffortTier.Standard,
            EffortPoints = 2,
            Status = ChoreStatus.Active,
            EnteredByUserId = Alice,
            AssignmentKind = AssignmentKind.None,
            CreatedAt = Utc0(2026, 1, 1),
        }));

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 4, now: Utc0(2026, 6, 24, 12));

        history.Ghosts.Should().BeEmpty();
        history.GoneQuiet.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════
    // WP-03 — fold the ChoreSnoozeEvent log into the accurate snoozed-vs-slipped reason. Seam-style tests
    // through GetHistoryAsync with real ChoreSnoozeEvent rows (the new read-path). now = Wed 2026-06-24,
    // Utc tz (a DateOnly snooze floor's LocalMidnightUtc = its own midnight — keeps interval math legible).
    // ═══════════════════════════════════════════════════════════════════════════════════════════════

    private static ChoreSnoozeEvent SnoozeEvent(int id, int choreId, DateTime setAtUtc, DateOnly? snoozedUntil) =>
        new()
        {
            HouseholdId = H1,
            ChoreId = choreId,
            SnoozeEventId = id,
            SetByUserId = Alice,
            SetAt = setAtUtc,
            SnoozedUntil = snoozedUntil,
        };

    private static HistoryGhost Ghost(ChoreHistoryResult h, string localDate) =>
        h.Ghosts.Single(g => g.ExpectedLocalDate == localDate);

    [Fact]
    public async Task Reason_SetThenClear_SnoozedInsideInterval_SlippedAfterClear()
    {
        // Fixed weekly Wed, never completed. Beats Jun 3/10/17/24. SET(until Jun 20) at Jun 8, CLEAR at Jun 15.
        // Interval [Jun 8, Jun 15) (cut short by the CLEAR before the floor). Current state = cleared (null).
        Seed(ctx =>
        {
            ctx.Chores.Add(WeeklyChore(1, "Trash", ChoreDaysOfWeek.Wednesday, snoozedUntil: null));
            ctx.ChoreSnoozeEvents.AddRange(
                SnoozeEvent(1, 1, Utc0(2026, 6, 8, 9), new DateOnly(2026, 6, 20)),  // SET
                SnoozeEvent(2, 1, Utc0(2026, 6, 15, 9), null));                     // CLEAR
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 4, now: Utc0(2026, 6, 24, 12));

        Ghost(history, "2026-06-10").Reason.Should().Be("snoozed", "Jun 10 is inside [Jun 8, Jun 15)");
        Ghost(history, "2026-06-10").ReasonFromLog.Should().BeTrue();
        Ghost(history, "2026-06-17").Reason.Should().Be("slipped", "the CLEAR cut the interval before Jun 17");
        Ghost(history, "2026-06-17").ReasonFromLog.Should().BeTrue();
    }

    [Fact]
    public async Task Reason_SetNoClear_IntervalEndsAtFloor_NotOpenForever()
    {
        // SET(until Jun 12) at Jun 8, no CLEAR. Interval [Jun 8, Jun 12). A ghost after Jun 12 is 'slipped' —
        // the interval expired at the floor, it did NOT stay open forever.
        Seed(ctx =>
        {
            ctx.Chores.Add(WeeklyChore(1, "Trash", ChoreDaysOfWeek.Wednesday, snoozedUntil: new DateOnly(2026, 6, 12)));
            ctx.ChoreSnoozeEvents.Add(SnoozeEvent(1, 1, Utc0(2026, 6, 8, 9), new DateOnly(2026, 6, 12)));
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 4, now: Utc0(2026, 6, 24, 12));

        Ghost(history, "2026-06-10").Reason.Should().Be("snoozed", "Jun 10 is inside [Jun 8, Jun 12)");
        Ghost(history, "2026-06-17").Reason.Should().Be("slipped", "the interval ended at the Jun 12 floor");
        Ghost(history, "2026-06-17").ReasonFromLog.Should().BeTrue();
    }

    [Fact]
    public async Task Reason_PreWindowSeed_SnoozeStartedBeforeWindow_LabelsEarlyGhostSnoozed()
    {
        // SET(until Jul 1) at May 20 — BEFORE the window (Mon Jun 1). Without the pre-window seed read, the
        // early-window ghost Jun 3 would be mislabeled. The seed extends coverage: [May 20, Jul 1) covers Jun 3.
        Seed(ctx =>
        {
            ctx.Chores.Add(WeeklyChore(1, "Trash", ChoreDaysOfWeek.Wednesday, snoozedUntil: new DateOnly(2026, 7, 1)));
            ctx.ChoreSnoozeEvents.Add(SnoozeEvent(1, 1, Utc0(2026, 5, 20, 9), new DateOnly(2026, 7, 1)));
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 4, now: Utc0(2026, 6, 24, 12));

        Ghost(history, "2026-06-03").Reason.Should().Be("snoozed", "the pre-window snooze covers the early-window beat");
        Ghost(history, "2026-06-03").ReasonFromLog.Should().BeTrue("the pre-window seed makes it log-derived, not fallback");
    }

    [Fact]
    public async Task Reason_BeforeEarliestEvent_FallsBackToCurrentState_ReasonFromLogFalse()
    {
        // Only event is at Jun 15. A ghost on Jun 3 predates all log coverage ⇒ fallback to current
        // Chore.SnoozedUntil, best-effort (ReasonFromLog=false). Current state here is cleared ⇒ 'slipped'.
        Seed(ctx =>
        {
            ctx.Chores.Add(WeeklyChore(1, "Trash", ChoreDaysOfWeek.Wednesday, snoozedUntil: null));
            ctx.ChoreSnoozeEvents.Add(SnoozeEvent(1, 1, Utc0(2026, 6, 15, 9), null)); // a CLEAR at Jun 15
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 4, now: Utc0(2026, 6, 24, 12));

        var g = Ghost(history, "2026-06-03");
        g.ReasonFromLog.Should().BeFalse("Jun 3 is before the earliest logged event (Jun 15) — fallback path");
        g.Reason.Should().Be("slipped", "current state is cleared");
    }

    [Fact]
    public async Task GoneQuiet_AllTrailingMissesSnoozed_ReadsSnoozed()
    {
        // weeks=2 ⇒ window Mon Jun 15; beats Jun 17/24 (both missed ⇒ gone quiet). A pre-window SET(until Jul 1)
        // at Jun 10 covers both trailing misses ⇒ GoneQuiet.Reason = 'snoozed'.
        Seed(ctx =>
        {
            ctx.Chores.Add(WeeklyChore(1, "Trash", ChoreDaysOfWeek.Wednesday, snoozedUntil: new DateOnly(2026, 7, 1)));
            ctx.ChoreSnoozeEvents.Add(SnoozeEvent(1, 1, Utc0(2026, 6, 10, 9), new DateOnly(2026, 7, 1)));
        });

        var history = await CreateService(Utc).GetHistoryAsync(H1, weeks: 2, now: Utc0(2026, 6, 24, 12));

        history.GoneQuiet.Should().ContainSingle().Which.Reason.Should().Be("snoozed",
            "every trailing missed beat is covered by the snooze log");
    }
}
