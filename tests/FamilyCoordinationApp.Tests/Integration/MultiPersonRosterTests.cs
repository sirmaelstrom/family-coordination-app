using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using FixedTimeProvider = FamilyCoordinationApp.Tests.Services.FixedTimeProvider;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Real-Postgres seam for the multi-person NAMED ROSTER (rework). Proves the three things the InMemory unit
/// suites cannot: (R1) the assign → commit → complete write paths persist participation events that the board
/// FOLD derives into the right per-member states; (R2) the last-N-done recurrence carry-over (the previous
/// occurrence's doers reappear as Assigned defaults) over real persistence; (R3) two concurrent roster writes
/// at the SAME xmin version serialize on the single Chore-row frontier (one wins, the loser gets a 409) —
/// the load-bearing concurrency guarantee (E1), reusing the exact <see cref="SaveBarrier"/> /
/// <see cref="GatedPostgresDbContextFactory"/> mechanism as the co-sign race.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class MultiPersonRosterTests(PostgresContainerFixture postgres)
{
    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    // ── R1: assign / commit / complete round-trip → board roster ────────────────────────────────────
    [Fact]
    public async Task AssignCommitComplete_ReflectedAsRosterStatesOnBoard_OnRealPostgres()
    {
        const int hh = 1, alice = 1, bob = 2, carol = 3, choreId = 1;
        var dbFactory = new PostgresDbContextFactory(await postgres.CreateDatabaseConnectionStringAsync());
        await SeedAsync(dbFactory, hh, choreId, RecurrenceMode.Flexible, requiredCount: 2, alice, bob, carol);

        var clock = new FixedTimeProvider(T0);
        var svc = NewService(dbFactory, clock);

        // Alice assigns Bob; Carol opts in (commits herself); Bob completes his part (partial — 1 of 2).
        var v0 = await ReadVersionAsync(dbFactory, hh, choreId);
        var afterAssign = await svc.AssignToRosterAsync(hh, choreId, actorUserId: alice, subjectUserId: bob, v0);
        clock.SetUtcNow(T0.AddMinutes(1));
        var afterCommit = await svc.CommitToRosterAsync(hh, choreId, actorUserId: carol, afterAssign.Version);
        clock.SetUtcNow(T0.AddMinutes(2));
        await svc.CompleteAsync(hh, choreId, actorUserId: bob, note: null, photoPath: null, participantUserIds: null, afterCommit.Version);

        var board = await NewBoardService(dbFactory, clock).GetBoardAsync(hh, alice, T0.AddMinutes(3));
        var dto = board.Chores.Single(c => c.Id == choreId);

        dto.RequiredCount.Should().Be(2);
        dto.CompletedCount.Should().Be(1, "only Bob has completed (1 of 2) — still Active");
        dto.Roster.Should().ContainSingle(m => m.UserId == bob && m.State == RosterState.Done);
        dto.Roster.Should().ContainSingle(m => m.UserId == carol && m.State == RosterState.In);
    }

    // ── R2: last-N-done recurrence carry-over ───────────────────────────────────────────────────────
    [Fact]
    public async Task PreviousOccurrenceDoers_SeedNextOccurrenceAsAssignedDefaults_OnRealPostgres()
    {
        const int hh = 1, alice = 1, amy = 2, kai = 3, choreId = 1;
        var dbFactory = new PostgresDbContextFactory(await postgres.CreateDatabaseConnectionStringAsync());
        await SeedAsync(dbFactory, hh, choreId, RecurrenceMode.Flexible, requiredCount: 2, alice, amy, kai);

        var clock = new FixedTimeProvider(T0);
        var svc = NewService(dbFactory, clock);

        // Occurrence 1: Alice (partial) then Amy (satisfying) → advances (Flexible stays Active).
        var v0 = await ReadVersionAsync(dbFactory, hh, choreId);
        var c1 = await svc.CompleteAsync(hh, choreId, alice, null, null, null, v0);
        clock.SetUtcNow(T0.AddMinutes(1));
        await svc.CompleteAsync(hh, choreId, amy, null, null, null, c1.Version);

        var board = await NewBoardService(dbFactory, clock).GetBoardAsync(hh, alice, T0.AddMinutes(2));
        var dto = board.Chores.Single(c => c.Id == choreId);

        dto.CompletedCount.Should().Be(0, "no one has completed the NEW occurrence yet");
        dto.Roster.Should().HaveCount(2);
        dto.Roster.Should().OnlyContain(m => m.State == RosterState.Assigned,
            "the previous occurrence's doers carry over as soft Assigned defaults (last-N-done)");
        dto.Roster.Select(m => m.UserId).Should().BeEquivalentTo(new[] { alice, amy });
    }

    // ── R3: two concurrent roster writes serialize on the Chore-row xmin (E1) ───────────────────────
    [Fact]
    public async Task TwoConcurrentCommits_SameVersion_SerializeViaXmin_OnRealPostgres()
    {
        const int hh = 1, alice = 1, amy = 2, choreId = 1;
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        var dbFactory = new PostgresDbContextFactory(connectionString);
        await SeedAsync(dbFactory, hh, choreId, RecurrenceMode.OneOff, requiredCount: 2, alice, amy);

        var version = await ReadVersionAsync(dbFactory, hh, choreId);
        version.Should().NotBe(0u, "real Postgres assigns a non-zero xmin to a committed row");

        // Both commits load the chore at `version`, block at SaveChanges, then race to commit.
        var barrier = new SaveBarrier(participants: 2);
        var gated = new GatedPostgresDbContextFactory(connectionString, barrier);
        var svc = NewService(gated, new FixedTimeProvider(T0.AddSeconds(1)));

        var a = AttemptCommitAsync(svc, hh, choreId, alice, version);
        var b = AttemptCommitAsync(svc, hh, choreId, amy, version);
        var results = await Task.WhenAll(a, b);

        results.Count(r => r.Won).Should().Be(1,
            "exactly one commit succeeds at the shared version — two wins would mean xmin did NOT serialize the roster writes");
        results.Count(r => r.Conflicted).Should().Be(1,
            "the loser MUST get a ChoreConflictException once the winner bumped xmin (M3/E1)");

        // Exactly one participation event landed (the winner's); the loser would re-submit against the refreshed version.
        await using var ctx = dbFactory.CreateDbContext();
        var events = await ctx.ChoreParticipationEvents.AsNoTracking()
            .Where(e => e.HouseholdId == hh && e.ChoreId == choreId).ToListAsync();
        events.Should().ContainSingle("only the winning commit persisted its participation event");
        events[0].Type.Should().Be(ChoreParticipationType.Committed);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static ChoreService NewService(
        IDbContextFactory<ApplicationDbContext> factory, FixedTimeProvider clock) =>
        new(factory, new ChoreStatusCalculator(), Mock.Of<IImageService>(), clock, NullLogger<ChoreService>.Instance);

    private static ChoreBoardService NewBoardService(
        IDbContextFactory<ApplicationDbContext> factory, FixedTimeProvider clock) =>
        new(factory, new ChoreStatusCalculator(), TimeZoneInfo.Utc, clock);

    private static async Task SeedAsync(
        PostgresDbContextFactory dbFactory, int hh, int choreId, RecurrenceMode mode, int requiredCount, params int[] userIds)
    {
        await using var ctx = dbFactory.CreateDbContext();
        await ctx.Database.EnsureCreatedAsync();
        ctx.Households.Add(new Household { Id = hh, Name = "Roster House", CreatedAt = T0 });
        foreach (var uid in userIds)
        {
            ctx.Users.Add(new User
            {
                Id = uid,
                HouseholdId = hh,
                Email = $"u{uid}@roster.test",
                DisplayName = $"User{uid}",
                Initials = "U",
                IsWhitelisted = true,
                CreatedAt = T0
            });
        }
        ctx.Chores.Add(new Chore
        {
            HouseholdId = hh,
            ChoreId = choreId,
            Name = "Roster target",
            RecurrenceMode = mode,
            IntervalDays = mode == RecurrenceMode.OneOff ? null : 7,
            AnchorDate = mode == RecurrenceMode.OneOff ? new DateOnly(2026, 6, 10) : null,
            EffortTier = EffortTier.Standard,
            EffortPoints = ChoreEffort.PointsFor(EffortTier.Standard),
            RequiredCount = requiredCount,
            Status = ChoreStatus.Active,
            EnteredByUserId = userIds[0],
            AssignmentKind = AssignmentKind.None,
            CreatedAt = T0
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<uint> ReadVersionAsync(PostgresDbContextFactory dbFactory, int hh, int choreId)
    {
        await using var ctx = dbFactory.CreateDbContext();
        var chore = await ctx.Chores.AsNoTracking().SingleAsync(c => c.HouseholdId == hh && c.ChoreId == choreId);
        return chore.Version;
    }

    private static async Task<RosterAttempt> AttemptCommitAsync(ChoreService svc, int hh, int choreId, int actor, uint version)
    {
        try
        {
            await svc.CommitToRosterAsync(hh, choreId, actor, version);
            return new RosterAttempt(Won: true, Conflicted: false);
        }
        catch (ChoreConflictException)
        {
            return new RosterAttempt(Won: false, Conflicted: true);
        }
    }

    private sealed record RosterAttempt(bool Won, bool Conflicted);
}
