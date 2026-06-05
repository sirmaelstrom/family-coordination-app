using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Unit tests for the pure <see cref="ChoreRosterCalculator"/> fold (V1). No DB, no I/O — events and
/// completions are constructed in memory and folded directly. Covers the window, the last-N-done
/// carry-over base, the monotonic event application (Assigned never downgrades In; Left removes), and the
/// Done overlay (anyone may complete; Done wins over a same-occurrence Left).
/// </summary>
public class ChoreRosterCalculatorTests
{
    private static DateTime Utc(int day, int hour = 12) => new(2026, 6, day, hour, 0, 0, DateTimeKind.Utc);

    private static ChoreParticipationEvent Ev(ChoreParticipationType type, int subject, DateTime at, int id, int? actor = null) =>
        new()
        {
            HouseholdId = 1,
            ChoreId = 1,
            ParticipationEventId = id,
            Type = type,
            SubjectUserId = subject,
            ActorUserId = actor ?? subject,
            At = at
        };

    private static ChoreCompletion Done(int user, DateTime at, int id) =>
        new()
        {
            HouseholdId = 1,
            ChoreId = 1,
            CompletionId = id,
            CompletedByUserId = user,
            CompletedAt = at,
            EffortPointsSnapshot = 1
        };

    private static DerivedRoster Fold(
        IEnumerable<ChoreParticipationEvent> events,
        IEnumerable<ChoreCompletion> completions,
        DateTime? lastCompletedAt = null,
        int requiredCount = 2,
        bool recurring = false) =>
        ChoreRosterCalculator.Fold(events, completions, lastCompletedAt, requiredCount, recurring);

    // ---- Empty / base cases --------------------------------------------------------------------------

    [Fact]
    public void NoEventsNoCompletions_EmptyRoster()
    {
        var result = Fold([], []);
        result.Members.Should().BeEmpty();
        result.CompletedCount.Should().Be(0);
    }

    [Fact]
    public void SingleAssigned_ShowsAssigned()
    {
        var result = Fold([Ev(ChoreParticipationType.Assigned, 100, Utc(1), 1, actor: 999)], []);
        result.Members.Should().BeEquivalentTo(new[] { new RosterMemberDto(100, RosterState.Assigned) });
    }

    [Fact]
    public void SelfCommitted_ShowsIn()
    {
        var result = Fold([Ev(ChoreParticipationType.Committed, 100, Utc(1), 1)], []);
        result.Members.Should().BeEquivalentTo(new[] { new RosterMemberDto(100, RosterState.In) });
    }

    // ---- Monotonic event application -----------------------------------------------------------------

    [Fact]
    public void AssignedThenCommitted_UpgradesToIn()
    {
        var result = Fold(
            [Ev(ChoreParticipationType.Assigned, 100, Utc(1), 1, actor: 999),
             Ev(ChoreParticipationType.Committed, 100, Utc(2), 2)],
            []);
        result.Members.Should().BeEquivalentTo(new[] { new RosterMemberDto(100, RosterState.In) });
    }

    [Fact]
    public void CommittedThenAssigned_DoesNotDowngrade()
    {
        // A stray later Assigned (e.g. a re-assign) must NOT knock an in member back to assigned.
        var result = Fold(
            [Ev(ChoreParticipationType.Committed, 100, Utc(1), 1),
             Ev(ChoreParticipationType.Assigned, 100, Utc(2), 2, actor: 999)],
            []);
        result.Members.Should().BeEquivalentTo(new[] { new RosterMemberDto(100, RosterState.In) });
    }

    [Fact]
    public void AssignedThenLeft_RemovesMember()
    {
        var result = Fold(
            [Ev(ChoreParticipationType.Assigned, 100, Utc(1), 1, actor: 999),
             Ev(ChoreParticipationType.Left, 100, Utc(2), 2)],
            []);
        result.Members.Should().BeEmpty();
    }

    [Fact]
    public void LeftThenReAssigned_ReAddsAsAssigned()
    {
        var result = Fold(
            [Ev(ChoreParticipationType.Committed, 100, Utc(1), 1),
             Ev(ChoreParticipationType.Left, 100, Utc(2), 2),
             Ev(ChoreParticipationType.Assigned, 100, Utc(3), 3, actor: 999)],
            []);
        result.Members.Should().BeEquivalentTo(new[] { new RosterMemberDto(100, RosterState.Assigned) });
    }

    [Fact]
    public void EventsOrderedByAtThenId_NotInsertionOrder()
    {
        // Provide events out of chronological order; the fold must order by At then id.
        var result = Fold(
            [Ev(ChoreParticipationType.Left, 100, Utc(3), 3),
             Ev(ChoreParticipationType.Assigned, 100, Utc(1), 1, actor: 999),
             Ev(ChoreParticipationType.Committed, 100, Utc(2), 2)],
            []);
        result.Members.Should().BeEmpty(); // assigned -> committed -> left
    }

    // ---- Done overlay --------------------------------------------------------------------------------

    [Fact]
    public void Completion_OverlaysDone_AndCounts()
    {
        var result = Fold(
            [Ev(ChoreParticipationType.Assigned, 100, Utc(1), 1, actor: 999),
             Ev(ChoreParticipationType.Committed, 101, Utc(1), 2)],
            [Done(100, Utc(2), 1)]);
        result.Members.Should().BeEquivalentTo(new[]
        {
            new RosterMemberDto(100, RosterState.Done),
            new RosterMemberDto(101, RosterState.In)
        });
        result.CompletedCount.Should().Be(1);
    }

    [Fact]
    public void CompleterNotOnRoster_AddedAsDone()
    {
        // Anyone may complete toward X even if never assigned/committed.
        var result = Fold([], [Done(200, Utc(2), 1)]);
        result.Members.Should().BeEquivalentTo(new[] { new RosterMemberDto(200, RosterState.Done) });
        result.CompletedCount.Should().Be(1);
    }

    [Fact]
    public void DoneWinsOverSameOccurrenceLeft()
    {
        // Completed then left: the work counts — Done overlay wins (operator-accepted).
        var result = Fold(
            [Ev(ChoreParticipationType.Left, 100, Utc(3), 1)],
            [Done(100, Utc(2), 1)]);
        result.Members.Should().BeEquivalentTo(new[] { new RosterMemberDto(100, RosterState.Done) });
        result.CompletedCount.Should().Be(1);
    }

    // ---- Window exclusion ----------------------------------------------------------------------------

    [Fact]
    public void EventsBeforeLastCompletedAt_Excluded()
    {
        // A previous-occurrence Assigned (At <= lastCompletedAt) must NOT leak into the new occurrence.
        var lastCompleted = Utc(10);
        var result = Fold(
            [Ev(ChoreParticipationType.Assigned, 100, Utc(5), 1, actor: 999),   // previous occurrence
             Ev(ChoreParticipationType.Committed, 101, Utc(12), 2)],            // current occurrence
            [],
            lastCompletedAt: lastCompleted);
        result.Members.Should().BeEquivalentTo(new[] { new RosterMemberDto(101, RosterState.In) });
    }

    [Fact]
    public void CompletionsBeforeLastCompletedAt_NotCountedDone()
    {
        var lastCompleted = Utc(10);
        var result = Fold([], [Done(100, Utc(5), 1)], lastCompletedAt: lastCompleted, recurring: false);
        result.CompletedCount.Should().Be(0);
        result.Members.Should().BeEmpty();
    }

    // ---- Last-N-done carry-over (recurrence) ---------------------------------------------------------

    [Fact]
    public void RecurringWithPriorCompleters_SeedsLastNDoneAsAssigned()
    {
        var lastCompleted = Utc(10);
        var result = Fold(
            [],
            [Done(100, Utc(9), 1), Done(101, Utc(10), 2)],   // previous occurrence's two doers
            lastCompletedAt: lastCompleted,
            requiredCount: 2,
            recurring: true);
        result.Members.Should().BeEquivalentTo(new[]
        {
            new RosterMemberDto(100, RosterState.Assigned),
            new RosterMemberDto(101, RosterState.Assigned)
        });
        result.CompletedCount.Should().Be(0); // none done in the NEW occurrence yet
    }

    [Fact]
    public void NonRecurring_DoesNotSeedDefaults()
    {
        var lastCompleted = Utc(10);
        var result = Fold(
            [],
            [Done(100, Utc(9), 1), Done(101, Utc(10), 2)],
            lastCompletedAt: lastCompleted,
            requiredCount: 2,
            recurring: false);
        result.Members.Should().BeEmpty();
    }

    [Fact]
    public void LastNDone_TakesMostRecentDistinct_UpToRequiredCount()
    {
        var lastCompleted = Utc(20);
        // Older occurrence done by {1,2}; most recent done by {3,4}. requiredCount=2 -> {3,4}.
        var completions = new[]
        {
            Done(1, Utc(5), 1), Done(2, Utc(6), 2),
            Done(3, Utc(18), 3), Done(4, Utc(19), 4)
        };
        var defaults = ChoreRosterCalculator.LastNDoneDefaults(completions, lastCompleted, requiredCount: 2);
        defaults.Should().BeEquivalentTo(new[] { 4, 3 }, o => o.WithStrictOrdering()); // most-recent first
    }

    [Fact]
    public void LeftRemovesACarriedDefault()
    {
        // A carried-over default who declines the new occurrence is removed.
        var lastCompleted = Utc(10);
        var result = Fold(
            [Ev(ChoreParticipationType.Left, 101, Utc(11), 5)],
            [Done(100, Utc(9), 1), Done(101, Utc(10), 2)],
            lastCompletedAt: lastCompleted,
            requiredCount: 2,
            recurring: true);
        result.Members.Should().BeEquivalentTo(new[] { new RosterMemberDto(100, RosterState.Assigned) });
    }

    [Fact]
    public void MembersOrderedByUserId()
    {
        var result = Fold(
            [Ev(ChoreParticipationType.Committed, 300, Utc(1), 1),
             Ev(ChoreParticipationType.Committed, 100, Utc(1), 2),
             Ev(ChoreParticipationType.Committed, 200, Utc(1), 3)],
            []);
        result.Members.Select(m => m.UserId).Should().ContainInOrder(100, 200, 300);
    }
}
