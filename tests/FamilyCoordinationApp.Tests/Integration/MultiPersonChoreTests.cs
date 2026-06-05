using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The real-Postgres seam for the multi-person (co-sign) chore feature (WP-03). Proves the FOUR things the
/// InMemory unit suite (WP-02) physically cannot, because InMemory has no <c>xmin</c> system column and
/// silently ignores the rowversion:
/// <list type="number">
///   <item><description><b>V1 — end-to-end co-sign gate</b> through the real HTTP endpoint + real save path:
///   a <c>requiredCount=2</c> OneOff chore advances to <c>Done</c> only when the SECOND distinct member
///   contributes; after the first (partial) contribution the board still lists it with
///   <c>completedCount=1</c>.</description></item>
///   <item><description><b>D3 — xmin actually bumps on a partial</b>: a single partial contribution changes
///   the chore <c>version</c> (the forced <c>LastContributionAt</c> write makes EF emit the UPDATE). Without
///   this bump the two racers could not serialize.</description></item>
///   <item><description><b>V3 — two-racer serialization (the load-bearing test)</b>: two <c>CompleteAsync</c>
///   calls as DIFFERENT users on the SAME loaded <c>version</c>, forced to race via the same
///   <see cref="SaveBarrier"/> / <see cref="GatedPostgresDbContextFactory"/> mechanism the claim-race uses
///   (<see cref="ChoreServiceConcurrencyTests"/>). Postgres bumps xmin on the first commit, so the loser MUST
///   get a <see cref="ChoreConflictException"/>; the loser then RE-SUBMITS against the refreshed version (the
///   island does not auto-retry) and adds the 2nd distinct row → exactly TWO distinct contribution rows and
///   exactly ONE advance. The FORBIDDEN states are asserted absent: never "2 rows, not advanced" and never
///   "advanced twice".</description></item>
///   <item><description><b>V6 — distinctness across the wire</b>: the same member completing twice is a
///   non-retryable 400 (not a 409), and the board still shows <c>completedCount=1</c> (no second
///   row).</description></item>
/// </list>
/// <para><b>Harness reuse (no parallel harness):</b> V1/D3/V6 run through <see cref="ChoresWebAppFactory"/>
/// (the full-host HTTP harness, header auth via <c>X-Test-User</c>, two seeded household-A members A/A2). V3
/// runs at the service layer over <see cref="PostgresDbContextFactory"/> /
/// <see cref="GatedPostgresDbContextFactory"/> on its OWN <c>EnsureCreated</c> database — the ONLY way to
/// force a DETERMINISTIC write-write race at the same <c>version</c> with a controllable
/// <see cref="FixedTimeProvider"/> (the HTTP host's clock is frozen and not advanceable per-call). This
/// mirrors <see cref="ChoreServiceConcurrencyTests"/> exactly.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class MultiPersonChoreTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── HTTP harness (V1/D3/V6) — the seeded full host with household A's two members A / A2. ──
    private readonly ChoresWebAppFactory _factory = new(postgres);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // The slice of the board ChoreDto this suite reads (camelCase, mirrors ChoreDtos.cs). Board excludes
    // Status==Done, so a Done chore is simply ABSENT from `chores` — there is no `status` field to read.
    private sealed record BoardChore(
        int id,
        uint version,
        string assignmentKind,
        int? assigneeUserId,
        DateTime? lastCompletedAt,
        int requiredCount,
        int completedCount,
        IReadOnlyList<int> contributorUserIds);

    private sealed record Board(List<BoardChore> chores);

    private async Task<Board> GetBoardAsync(HttpClient client)
    {
        var board = await client.GetFromJsonAsync<Board>("/api/chores/board", Json);
        board.Should().NotBeNull();
        return board!;
    }

    private static BoardChore? FindChore(Board board, int choreId) =>
        board.chores.SingleOrDefault(c => c.id == choreId);

    /// <summary>Create a requiredCount=2 OneOff chore through the real endpoint and return its DTO.</summary>
    private static async Task<BoardChore> CreateCoSignOneOffAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/chores/", new
        {
            name,
            recurrenceMode = "oneOff",
            effortTier = "standard",
            requiredCount = 2
        }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "a valid co-sign OneOff create must succeed");
        var created = await resp.Content.ReadFromJsonAsync<BoardChore>(Json);
        created.Should().NotBeNull();
        created!.requiredCount.Should().Be(2, "the create request carried requiredCount=2");
        return created;
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, int choreId, uint version) =>
        client.PostAsync($"/api/chores/{choreId}/complete",
            JsonContent.Create(new { version }, options: Json));

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // V1 — end-to-end co-sign gate through the HTTP endpoint against REAL Postgres.
    // ──────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CoSignOneOff_AdvancesToDoneOnly_WhenSecondDistinctMemberCompletes_OnRealPostgres()
    {
        var clientA = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var clientA2 = _factory.CreateClientAs(ChoresWebAppFactory.UserA2Email);

        // Create a requiredCount=2 OneOff chore in household A.
        var created = await CreateCoSignOneOffAsync(clientA, "Co-sign OneOff (V1)");
        var choreId = created.id;

        // First (partial) contribution by member A. The chore must STILL be on the board, requiredCount=2,
        // completedCount=1, A listed as the lone contributor, and NOT yet advanced (no LastCompletedAt).
        var firstResp = await CompleteAsync(clientA, choreId, created.version);
        firstResp.StatusCode.Should().Be(HttpStatusCode.OK, "the first contribution toward a co-sign chore succeeds");

        var afterFirstBoard = await GetBoardAsync(clientA);
        var afterFirst = FindChore(afterFirstBoard, choreId);
        afterFirst.Should().NotBeNull("a partial co-sign OneOff must remain on the board (not yet Done)");
        afterFirst!.requiredCount.Should().Be(2);
        afterFirst.completedCount.Should().Be(1, "exactly one distinct member has contributed so far");
        afterFirst.contributorUserIds.Should().BeEquivalentTo(new[] { ChoresWebAppFactory.UserAId });
        afterFirst.lastCompletedAt.Should().BeNull("a partial contribution must NOT advance the chore (D4)");

        // Second distinct member (A2) contributes against the chore's CURRENT version (fresh read off the board).
        var secondResp = await CompleteAsync(clientA2, choreId, afterFirst.version);
        secondResp.StatusCode.Should().Be(HttpStatusCode.OK, "the satisfying contribution succeeds");

        // The board no longer lists the chore: a OneOff that reaches RequiredCount goes Status=Done, and the
        // board excludes Done. (Absence from the board IS the Status=Done assertion — there is no Done card.)
        var afterSecondBoard = await GetBoardAsync(clientA);
        FindChore(afterSecondBoard, choreId).Should()
            .BeNull("a satisfied co-sign OneOff advances to Status=Done, which the board excludes");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // D3 — the xmin token actually bumps on a PARTIAL contribution (the forced LastContributionAt write).
    // ──────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task PartialContribution_BumpsXminVersion_OnRealPostgres()
    {
        var clientA = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        // requiredCount=2 so a single contribution is a PARTIAL (does not advance / does not go Done).
        var created = await CreateCoSignOneOffAsync(clientA, "Co-sign OneOff (D3)");
        var choreId = created.id;
        var versionBefore = created.version;

        var resp = await CompleteAsync(clientA, choreId, versionBefore);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var board = await GetBoardAsync(clientA);
        var after = FindChore(board, choreId);
        after.Should().NotBeNull("still partial → still on the board");
        after!.lastCompletedAt.Should().BeNull("the contribution was partial (not yet satisfying)");

        // THE assertion: even though only ChoreCompletion rows + LastContributionAt changed (no advance), the
        // forced LastContributionAt write made EF emit the UPDATE, so Postgres bumped xmin. Without this bump
        // the two-racer serialization in V3 is impossible.
        after.version.Should().NotBe(versionBefore,
            "a partial contribution must bump xmin via the forced LastContributionAt UPDATE (D3) — " +
            "without it concurrent contributions cannot serialize (E2)");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // V3 — TWO-RACER SERIALIZATION (the load-bearing test), service-level on real Postgres.
    //
    // Reuses the EXACT mechanism from ChoreServiceConcurrencyTests: a SaveBarrier rendezvous +
    // GatedPostgresDbContextFactory so both CompleteAsync calls reach SaveChanges after both loaded the chore
    // at the SAME xmin version. Because each forces a Chore UPDATE (LastContributionAt), Postgres bumps xmin
    // on the first commit → the second MUST throw ChoreConflictException. The loser re-submits against the
    // refreshed version. End state: exactly 2 distinct contribution rows, exactly one advance (Status=Done).
    // ──────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TwoConcurrentContributions_SameVersion_DifferentUsers_SerializeViaXmin_OnRealPostgres()
    {
        const int householdId = 1;
        const int alice = 1;   // first distinct contributor
        const int amy = 2;     // second distinct contributor
        const int choreId = 1;
        var t0 = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // Own EnsureCreated database on the shared container (cannot share the MigrateAsync HTTP DB) — exactly
        // as ChoreServiceConcurrencyTests does.
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        var dbFactory = new PostgresDbContextFactory(connectionString);

        await using (var ctx = dbFactory.CreateDbContext())
        {
            await ctx.Database.EnsureCreatedAsync();
            ctx.Households.Add(new Household { Id = householdId, Name = "Race House", CreatedAt = t0 });
            ctx.Users.AddRange(
                new User { Id = alice, HouseholdId = householdId, Email = "alice@race.test", DisplayName = "Alice", Initials = "A", IsWhitelisted = true, CreatedAt = t0 },
                new User { Id = amy, HouseholdId = householdId, Email = "amy@race.test", DisplayName = "Amy", Initials = "AM", IsWhitelisted = true, CreatedAt = t0 });
            // requiredCount=2 OneOff: two DISTINCT contributors satisfy it; advance ⇒ Status=Done.
            ctx.Chores.Add(new Chore
            {
                HouseholdId = householdId,
                ChoreId = choreId,
                Name = "Co-sign race target",
                RecurrenceMode = RecurrenceMode.OneOff,
                EffortTier = EffortTier.Standard,
                EffortPoints = ChoreEffort.PointsFor(EffortTier.Standard),
                RequiredCount = 2,
                Status = ChoreStatus.Active,
                EnteredByUserId = alice,
                AssignmentKind = AssignmentKind.None,
                CreatedAt = t0
            });
            await ctx.SaveChangesAsync();
        }

        // A controllable clock so we can advance time BETWEEN contributions for realism (correctness must NOT
        // depend on the value differing — WP-02 forces the row modified regardless).
        var clock = new FamilyCoordinationApp.Tests.Services.FixedTimeProvider(t0);

        var version = await ReadVersionAsync(dbFactory, householdId, choreId);
        version.Should().NotBe(0u, "real Postgres assigns a non-zero xmin to a committed row");

        // The gated racing service: both contributions block at SaveChanges until both have arrived (after
        // both loaded the chore at `version`), then commit — a genuine write-write race at the same version.
        var barrier = new SaveBarrier(participants: 2);
        var gatedFactory = new GatedPostgresDbContextFactory(connectionString, barrier);
        var racingService = NewService(gatedFactory, clock);

        clock.SetUtcNow(t0.AddSeconds(1)); // advance the clock for the racing contributions (realism only)
        var attemptAlice = AttemptContributeAsync(racingService, householdId, choreId, alice, version);
        var attemptAmy = AttemptContributeAsync(racingService, householdId, choreId, amy, version);
        var results = await Task.WhenAll(attemptAlice, attemptAmy);

        var winners = results.Count(r => r.Won);
        var conflicts = results.Count(r => r.Conflicted);
        winners.Should().Be(1,
            "exactly one racer commits at the shared version — two wins would mean xmin did NOT serialize the " +
            "partials (the 'both commit from the same version' state the spec forbids)");
        conflicts.Should().Be(1,
            "the loser MUST get a ChoreConflictException once the winner bumped xmin (D3/V3)");
        results.Should().NotContain(r => r.OtherError,
            "the loser must lose on the xmin token, not the 'nothing new' (D6) validation guard");

        // After exactly ONE racer committed: exactly one contribution row, chore NOT advanced (1 of 2),
        // still Active. This is the intermediate state the loser will resolve by re-submitting.
        var winner = results.Single(r => r.Won);
        await AssertStateAsync(dbFactory, householdId, choreId,
            expectedRows: 1, expectedDistinct: 1, expectAdvancedDone: false);

        // ── The loser RE-SUBMITS against the refreshed version (the island does not auto-retry). This is the
        // SATISFYING contribution: it re-reads the contributor set under the now-serialized write, sees Alice
        // already counted, adds the 2nd distinct row (the loser's own user), and advances exactly once. ──
        var loser = results.Single(r => r.Conflicted);
        var refreshed = await ReadVersionAsync(dbFactory, householdId, choreId);
        clock.SetUtcNow(t0.AddSeconds(2)); // advance again before the re-submit (realism only)
        var nonRacingService = NewService(dbFactory, clock);
        var resubmit = await AttemptContributeAsync(nonRacingService, householdId, choreId, loser.UserId, refreshed);
        resubmit.Won.Should().BeTrue("the loser's re-submit against the refreshed version must succeed");

        // ── End state: EXACTLY 2 distinct contribution rows and EXACTLY ONE advance (Status=Done). ──
        await AssertStateAsync(dbFactory, householdId, choreId,
            expectedRows: 2, expectedDistinct: 2, expectAdvancedDone: true);

        // Belt-and-braces over the FORBIDDEN states (asserting them ABSENT, not merely that "a completion
        // happened"):
        await using var verify = dbFactory.CreateDbContext();
        var finalChore = await verify.Chores.AsNoTracking()
            .SingleAsync(c => c.HouseholdId == householdId && c.ChoreId == choreId);
        var finalRows = await verify.ChoreCompletions.AsNoTracking()
            .Where(c => c.HouseholdId == householdId && c.ChoreId == choreId)
            .ToListAsync();
        var distinctContributors = finalRows.Select(c => c.CompletedByUserId).Distinct().ToList();

        // FORBIDDEN: "2 distinct contributors recorded but NOT advanced" (the E2 wedge).
        (distinctContributors.Count == 2 && (finalChore.Status != ChoreStatus.Done || finalChore.LastCompletedAt is null))
            .Should().BeFalse("FORBIDDEN: 2 distinct contributors recorded but the chore did not advance (E2 wedge)");

        // FORBIDDEN: "advanced twice" — surfaced as MORE than RequiredCount distinct rows toward one
        // occurrence, or extra completion rows beyond the two distinct contributors.
        finalRows.Should().HaveCount(2, "FORBIDDEN: more than one row per distinct contributor ⇒ a double-advance / double-count");
        distinctContributors.Should().BeEquivalentTo(new[] { alice, amy },
            "the two distinct contributors are exactly Alice and Amy — one win + one re-submit");
        finalChore.Status.Should().Be(ChoreStatus.Done, "the satisfied co-sign OneOff advanced exactly once");
        finalChore.LastCompletedAt.Should().NotBeNull("the satisfying contribution set LastCompletedAt exactly once");
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────
    // V6 — distinctness across the wire: the SAME member completing twice is a non-retryable 400.
    // ──────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task SameMemberCompletesTwice_SecondIs400NotRetryable_BoardStillCompletedCountOne_OnRealPostgres()
    {
        var clientA = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var created = await CreateCoSignOneOffAsync(clientA, "Co-sign OneOff (V6)");
        var choreId = created.id;

        // First contribution by A succeeds (partial — completedCount=1).
        var first = await CompleteAsync(clientA, choreId, created.version);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterFirstBoard = await GetBoardAsync(clientA);
        var afterFirst = FindChore(afterFirstBoard, choreId);
        afterFirst.Should().NotBeNull();
        afterFirst!.completedCount.Should().Be(1);

        // SAME member completes again against the CURRENT (non-stale) version → the "nothing new" guard (D6)
        // rejects it as a 400, NOT a 409. This is the key distinction: a non-retryable client rejection, not
        // a concurrency conflict the island should retry.
        var second = await CompleteAsync(clientA, choreId, afterFirst.version);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the same member re-completing is a non-retryable distinctness rejection (400), never a 409 conflict");

        // No second row was written: the board still shows completedCount=1 with A as the lone contributor.
        var afterSecondBoard = await GetBoardAsync(clientA);
        var afterSecond = FindChore(afterSecondBoard, choreId);
        afterSecond.Should().NotBeNull("rejected re-completion must NOT have advanced/removed the chore");
        afterSecond!.completedCount.Should().Be(1, "the rejected duplicate added no second contribution row");
        afterSecond.contributorUserIds.Should().BeEquivalentTo(new[] { ChoresWebAppFactory.UserAId });
    }

    // ── service-level helpers (mirror ChoreServiceConcurrencyTests) ──────────────────────────────────

    private static ChoreService NewService(
        Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext> factory,
        FamilyCoordinationApp.Tests.Services.FixedTimeProvider clock) =>
        new(
            factory,
            new ChoreStatusCalculator(),
            Mock.Of<IImageService>(),
            clock,
            NullLogger<ChoreService>.Instance);

    private static async Task<uint> ReadVersionAsync(
        PostgresDbContextFactory dbFactory, int householdId, int choreId)
    {
        await using var ctx = dbFactory.CreateDbContext();
        var chore = await ctx.Chores.AsNoTracking()
            .SingleAsync(c => c.HouseholdId == householdId && c.ChoreId == choreId);
        return chore.Version;
    }

    /// <summary>
    /// One co-sign contribution attempt, classifying the outcome the same way
    /// <see cref="ChoreServiceConcurrencyTests"/> classifies a claim: a clean win, an xmin
    /// <see cref="ChoreConflictException"/> (the loser), or the "nothing new" <see cref="ChoreValidationException"/>
    /// guard (which here would mean the race did NOT serialize on xmin — surfaced so the assertion fails loudly).
    /// </summary>
    private static async Task<ContributeResult> AttemptContributeAsync(
        ChoreService service, int householdId, int choreId, int actorUserId, uint version)
    {
        try
        {
            await service.CompleteAsync(householdId, choreId, actorUserId,
                note: null, photoPath: null, participantUserIds: null, version);
            return new ContributeResult(actorUserId, Won: true, Conflicted: false, OtherError: false);
        }
        catch (ChoreConflictException)
        {
            return new ContributeResult(actorUserId, Won: false, Conflicted: true, OtherError: false);
        }
        catch (ChoreValidationException)
        {
            return new ContributeResult(actorUserId, Won: false, Conflicted: false, OtherError: true);
        }
    }

    private static async Task AssertStateAsync(
        PostgresDbContextFactory dbFactory, int householdId, int choreId,
        int expectedRows, int expectedDistinct, bool expectAdvancedDone)
    {
        await using var ctx = dbFactory.CreateDbContext();
        var chore = await ctx.Chores.AsNoTracking()
            .SingleAsync(c => c.HouseholdId == householdId && c.ChoreId == choreId);
        var rows = await ctx.ChoreCompletions.AsNoTracking()
            .Where(c => c.HouseholdId == householdId && c.ChoreId == choreId)
            .ToListAsync();

        rows.Should().HaveCount(expectedRows, "exactly one ChoreCompletion row is written per newly-contributing member");
        rows.Select(c => c.CompletedByUserId).Distinct().Should().HaveCount(expectedDistinct);

        if (expectAdvancedDone)
        {
            chore.Status.Should().Be(ChoreStatus.Done, "a satisfied co-sign OneOff advances to Done");
            chore.LastCompletedAt.Should().NotBeNull("the satisfying contribution sets LastCompletedAt");
        }
        else
        {
            chore.Status.Should().Be(ChoreStatus.Active, "a partial contribution leaves Status untouched (D4)");
            chore.LastCompletedAt.Should().BeNull("a partial contribution leaves LastCompletedAt untouched (D4)");
        }
    }

    private sealed record ContributeResult(int UserId, bool Won, bool Conflicted, bool OtherError);
}
