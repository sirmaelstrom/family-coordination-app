using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// THE operator-mandated centerpiece (VG1 / M7 / M12), verified against REAL PostgreSQL at the service layer:
/// the <c>xmin</c> optimistic-concurrency token on <c>Chore</c> surfaces a two-writer race as a
/// <see cref="ChoreConflictException"/> (which WP-06 maps to HTTP 409) — exactly one writer wins, the other is
/// rejected; never last-write-wins. This is the seam the InMemory provider CANNOT exercise: InMemory has no
/// <c>xmin</c> system column and silently ignores the rowversion, so both racers would "win" (the latent bug).
/// <para>This drives the REAL production <see cref="ChoreService"/> + <see cref="ApplicationDbContext"/>
/// against the Testcontainers Postgres, so the live save path (<c>SaveWithConcurrencyAsync</c> setting the
/// client token as the xmin <c>OriginalValue</c>, then the DB's <c>WHERE xmin = ...</c> matching zero rows on
/// the loser) is what is under test — not a mock and not the HTTP layer.</para>
/// <para><b>Why service-level rather than through the HTTP endpoints:</b> the HTTP host currently fails to
/// start due to two pre-existing production defects reported separately (see the WP-08 findings:
/// (1) <c>ChoresEndpoints.DeleteChore</c> uses <c>MapDelete</c> with an inferred request body, which .NET
/// rejects at endpoint build; (2) the orphaned <c>20260131232149_AddShoppingListFavorites</c> migration
/// breaks a fresh-DB <c>MigrateAsync</c>). Neither defect touches the concurrency mechanism, so this test
/// verifies the mandated primary risk directly against real Postgres without depending on the broken host.
/// The HTTP-level harness (<see cref="ChoresWebAppFactory"/> + the HTTP tests) is in place and will exercise
/// the same path end-to-end once those production defects are fixed.</para>
/// <para>The schema is materialized with <c>EnsureCreatedAsync</c> (full current model, including the
/// <c>xmin</c> rowversion mapping) to sidestep the broken migration chain — the concurrency behavior under
/// test is identical.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreServiceConcurrencyTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const int HouseholdId = 1;
    private const int Alice = 1;
    private const int Bob = 2;
    private const int PileChoreId = 1;

    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private PostgresDbContextFactory _dbFactory = default!;
    private ChoreService _service = default!;

    public async Task InitializeAsync()
    {
        _dbFactory = new PostgresDbContextFactory(postgres.ConnectionString);

        await using (var ctx = _dbFactory.CreateDbContext())
        {
            // Fresh schema per run from the current model (real Postgres, real xmin column). Drop first so the
            // suite is idempotent across reruns against the shared container.
            await ctx.Database.EnsureDeletedAsync();
            await ctx.Database.EnsureCreatedAsync();

            ctx.Households.Add(new Household { Id = HouseholdId, Name = "Race House", CreatedAt = Now });
            ctx.Users.AddRange(
                new User { Id = Alice, HouseholdId = HouseholdId, Email = "alice@race.test", DisplayName = "Alice", Initials = "A", IsWhitelisted = true, CreatedAt = Now },
                new User { Id = Bob, HouseholdId = HouseholdId, Email = "bob@race.test", DisplayName = "Bob", Initials = "B", IsWhitelisted = true, CreatedAt = Now });
            ctx.Chores.Add(new Chore
            {
                HouseholdId = HouseholdId,
                ChoreId = PileChoreId,
                Name = "Pile chore (race target)",
                RecurrenceMode = RecurrenceMode.Flexible,
                IntervalDays = 7,
                EffortTier = EffortTier.Standard,
                EffortPoints = ChoreEffort.PointsFor(EffortTier.Standard),
                Status = ChoreStatus.Active,
                EnteredByUserId = Alice,
                AssignmentKind = AssignmentKind.None,
                CreatedAt = Now
            });
            await ctx.SaveChangesAsync();
        }

        _service = new ChoreService(
            _dbFactory,
            new ChoreStatusCalculator(),
            Mock.Of<IImageService>(),
            new FamilyCoordinationApp.Tests.Services.FixedTimeProvider(Now),
            NullLogger<ChoreService>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<uint> ReadVersionAsync()
    {
        await using var ctx = _dbFactory.CreateDbContext();
        var chore = await ctx.Chores.AsNoTracking()
            .SingleAsync(c => c.HouseholdId == HouseholdId && c.ChoreId == PileChoreId);
        return chore.Version;
    }

    [Fact]
    public async Task TwoConcurrentClaims_SameStaleXminVersion_ExactlyOneWins_OneConflicts_OnRealPostgres()
    {
        // The two writers must BOTH load the chore while it is still unclaimed (both capturing the same xmin
        // version) and only THEN race their saves — otherwise whichever loads second would read the
        // post-commit state and be rejected by the "already held" validation guard instead of exercising the
        // xmin token. A save barrier forces the genuine write-write race: both ClaimAsync calls reach
        // SaveChanges before either commits, so exactly one wins on xmin and the other gets a real
        // DbUpdateConcurrencyException -> ChoreConflictException (the 409 path). This is precisely the seam
        // InMemory cannot test (it ignores the rowversion and would let BOTH win).
        var version = await ReadVersionAsync();
        version.Should().NotBe(0u, "real Postgres assigns a non-zero xmin to a committed row");

        var barrier = new SaveBarrier(participants: 2);
        var gatedFactory = new GatedPostgresDbContextFactory(postgres.ConnectionString, barrier);
        var racingService = new ChoreService(
            gatedFactory,
            new ChoreStatusCalculator(),
            Mock.Of<IImageService>(),
            new FamilyCoordinationApp.Tests.Services.FixedTimeProvider(Now),
            NullLogger<ChoreService>.Instance);

        var claimAlice = Attempt(() => racingService.ClaimAsync(HouseholdId, PileChoreId, Alice, version));
        var claimBob = Attempt(() => racingService.ClaimAsync(HouseholdId, PileChoreId, Bob, version));

        var results = await Task.WhenAll(claimAlice, claimBob);

        var wins = results.Count(r => r.Success);
        var conflicts = results.Count(r => r.Conflict);

        wins.Should().Be(1, "exactly one concurrent claim must win — two wins would mean xmin is not enforced (last-write-wins, the bug this verifies against)");
        conflicts.Should().Be(1, "exactly one concurrent claim must lose with a ChoreConflictException (the xmin 409 path)");
        results.Should().NotContain(r => r.OtherError, "the loser must be an xmin conflict, not the 'already held' validation guard");

        // The loser did NOT overwrite the winner: real DB state shows exactly one claimer with an advanced xmin.
        await using var ctx = _dbFactory.CreateDbContext();
        var after = await ctx.Chores.AsNoTracking()
            .SingleAsync(c => c.HouseholdId == HouseholdId && c.ChoreId == PileChoreId);
        after.AssignmentKind.Should().Be(AssignmentKind.Claimed);
        after.AssigneeUserId.Should().BeOneOf(Alice, Bob);
        after.Version.Should().NotBe(version, "the winning write advances the xmin token");

        // Exactly one Claimed event was appended (the winner's); the loser's transaction rolled back.
        var claimedEvents = await ctx.ChoreEvents.AsNoTracking()
            .CountAsync(e => e.HouseholdId == HouseholdId && e.ChoreId == PileChoreId && e.Type == ChoreEventType.Claimed);
        claimedEvents.Should().Be(1, "only the winning claim should persist its ChoreEvent");
    }

    [Fact]
    public async Task SecondClaim_WithStaleXminVersion_Throws_AfterFirstSucceeds_OnRealPostgres()
    {
        // Sequential proof of the same property: claim once (advances xmin), drop back to the pile, then a
        // second claim re-using the ORIGINAL (now stale) version must conflict — isolating the failure to the
        // stale xmin token, not the "already held" validation.
        var staleVersion = await ReadVersionAsync();

        var afterClaim = await _service.ClaimAsync(HouseholdId, PileChoreId, Alice, staleVersion);
        afterClaim.AssignmentKind.Should().Be(AssignmentKind.Claimed);

        await _service.DropAsync(HouseholdId, PileChoreId, Alice, afterClaim.Version);

        // Re-using the stale version must throw ChoreConflictException (→ HTTP 409), not silently succeed.
        var act = async () => await _service.ClaimAsync(HouseholdId, PileChoreId, Bob, staleVersion);
        await act.Should().ThrowAsync<ChoreConflictException>();
    }

    private static async Task<ClaimResult> Attempt(Func<Task<Chore>> claim)
    {
        try
        {
            await claim();
            return new ClaimResult(Success: true, Conflict: false, OtherError: false);
        }
        catch (ChoreConflictException)
        {
            return new ClaimResult(Success: false, Conflict: true, OtherError: false);
        }
        catch (ChoreValidationException)
        {
            // The loser was rejected by the "already held" guard rather than the xmin token — would mean the
            // save barrier failed to force a true write-write race. Surfaced so the assertion can fail loudly.
            return new ClaimResult(Success: false, Conflict: false, OtherError: true);
        }
    }

    private sealed record ClaimResult(bool Success, bool Conflict, bool OtherError);
}

/// <summary>
/// A two-participant rendezvous: each <see cref="ArriveAndWaitAsync"/> caller blocks until ALL participants
/// have arrived, then all proceed. Used to force two <c>ClaimAsync</c> calls to both reach <c>SaveChanges</c>
/// (after both have loaded the unclaimed row at the same xmin version) before either commits — a deterministic
/// write-write race against real Postgres. The gate fires once; later calls pass through immediately so the
/// loser's rollback/retry path is never re-blocked.
/// </summary>
public sealed class SaveBarrier(int participants)
{
    private readonly object _lock = new();
    private int _arrived;
    private bool _released;
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ArriveAndWaitAsync()
    {
        lock (_lock)
        {
            if (_released) return Task.CompletedTask;
            if (++_arrived >= participants)
            {
                _released = true;
                _all.TrySetResult();
            }
        }
        return _all.Task;
    }
}

/// <summary>
/// <see cref="PostgresDbContextFactory"/> variant that hands out <see cref="GatedApplicationDbContext"/>
/// instances sharing one <see cref="SaveBarrier"/>, so the first <c>SaveChangesAsync</c> on each context
/// rendezvouses before committing.
/// </summary>
public sealed class GatedPostgresDbContextFactory(string connectionString, SaveBarrier barrier)
    : IDbContextFactory<ApplicationDbContext>
{
    private DbContextOptions<ApplicationDbContext> BuildOptions() =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

    public ApplicationDbContext CreateDbContext() => new GatedApplicationDbContext(BuildOptions(), barrier);

    public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ApplicationDbContext>(new GatedApplicationDbContext(BuildOptions(), barrier));
}

/// <summary>
/// An <see cref="ApplicationDbContext"/> whose first <see cref="SaveChangesAsync(CancellationToken)"/> waits
/// on a shared <see cref="SaveBarrier"/> before delegating to the real save — forcing two concurrent claims to
/// both reach the write after both have read the unclaimed row at the same xmin version.
/// </summary>
public sealed class GatedApplicationDbContext(DbContextOptions<ApplicationDbContext> options, SaveBarrier barrier)
    : ApplicationDbContext(options)
{
    private bool _gated;

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!_gated)
        {
            _gated = true;
            await barrier.ArriveAndWaitAsync();
        }
        return await base.SaveChangesAsync(cancellationToken);
    }
}
