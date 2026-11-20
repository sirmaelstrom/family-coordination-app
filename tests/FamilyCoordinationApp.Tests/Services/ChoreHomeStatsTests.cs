using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Pure unit tests for <see cref="ChoreHomeStats.Compute"/> (V11, Home surface) — the count reducer extracted
/// from <c>Home.razor</c> so the snooze guard on up-for-grabs is testable without a Razor harness. A snoozed
/// chore already reads <c>Scheduled</c>, so it is auto-excluded from Overdue/DueToday; the explicit
/// <c>!IsSnoozed</c> guard exists only for the assignment-keyed up-for-grabs bucket.
/// </summary>
public class ChoreHomeStatsTests
{
    private const int Me = 7;
    private const int Other = 8;

    private static ChoreDto Dto(
        int id,
        DueState dueState,
        AssignmentKind kind,
        bool isSnoozed = false,
        bool isClaimStale = false,
        int requiredCount = 1,
        int completedCount = 0,
        params int[] roster) => new(
        Id: id,
        Name: $"Chore {id}",
        Icon: "",
        Description: null,
        RoomIds: [],
        RecurrenceMode: "Flexible",
        IntervalDays: 7,
        DaysOfWeek: null,
        AnchorDate: null,
        DueState: dueState,
        ColorTier: ColorTier.Fresh,
        NextDueAt: null,
        SnoozedUntil: isSnoozed ? new DateOnly(2026, 7, 1) : null,
        IsSnoozed: isSnoozed,
        IsClaimStale: isClaimStale,
        EffortTier: "Standard",
        EffortPoints: 2,
        OwnerUserId: null,
        AssigneeUserId: kind == AssignmentKind.None ? null : 1,
        AssignmentKind: kind,
        ClaimedAt: null,
        LastCompletedAt: null,
        PhotoPath: null,
        Version: 1,
        RequiredCount: requiredCount,
        CompletedCount: completedCount,
        Roster: roster.Select(u => new RosterMemberDto(u, RosterState.In)).ToList(),
        Subtasks: []);

    [Fact]
    public void Compute_ExcludesSnoozedFromUpForGrabs_WithClearedControl()
    {
        // The same unclaimed chore: snoozed → NOT up-for-grabs; un-snoozed → up-for-grabs.
        ChoreHomeStats.Compute(new[] { Dto(1, DueState.Scheduled, AssignmentKind.None, isSnoozed: true) }, Me)
            .UpForGrabs.Should().Be(0);

        ChoreHomeStats.Compute(new[] { Dto(1, DueState.Scheduled, AssignmentKind.None, isSnoozed: false) }, Me)
            .UpForGrabs.Should().Be(1);
    }

    [Fact]
    public void Compute_CountsAllFourBuckets_AndSnoozedReadsScheduledSoIsAutoExcluded()
    {
        var chores = new[]
        {
            Dto(1, DueState.Overdue, AssignmentKind.Assigned),                  // overdue, held => not up-for-grabs
            Dto(2, DueState.DueToday, AssignmentKind.None),                     // due today + unassigned => up-for-grabs
            Dto(3, DueState.Scheduled, AssignmentKind.None),                    // up-for-grabs only
            Dto(4, DueState.NotDue, AssignmentKind.Claimed, isClaimStale: true),// stale claim => up-for-grabs
            Dto(5, DueState.Scheduled, AssignmentKind.None, isSnoozed: true),   // snoozed => excluded everywhere
        };

        var r = ChoreHomeStats.Compute(chores, Me);

        r.Total.Should().Be(5);
        r.Overdue.Should().Be(1, "snoozed #5 reads Scheduled — it never inflates Overdue");
        r.DueToday.Should().Be(1, "snoozed #5 reads Scheduled — it never inflates DueToday");
        r.UpForGrabs.Should().Be(3, "#2, #3, #4 — NOT the snoozed #5 nor the assigned #1");
    }

    [Fact]
    public void Compute_MultiPersonChore_UsesTheBoardLensRosterRule()
    {
        // The board's Up for grabs lens (upForGrabsChores, frontend/app/src/lib/chores/lib/state.svelte.ts): an
        // unfinished multi-person chore is up for grabs only to a member not on its roster, whatever the
        // assignment trio says; a finished one falls back to the single-person rule; snoozed never counts.
        ChoreHomeStats.Compute(new[] { Dto(1, DueState.NotDue, AssignmentKind.None, requiredCount: 2, roster: [Me]) }, Me)
            .UpForGrabs.Should().Be(0, "I am on the roster, so the board shows it under Mine");
        ChoreHomeStats.Compute(new[] { Dto(1, DueState.NotDue, AssignmentKind.None, requiredCount: 2, roster: [Other]) }, Me)
            .UpForGrabs.Should().Be(1, "I am not on the roster of an unfinished two-person chore");
        ChoreHomeStats.Compute(new[] { Dto(1, DueState.NotDue, AssignmentKind.Assigned, requiredCount: 2, roster: [Other]) }, Me)
            .UpForGrabs.Should().Be(1, "the roster decides for a multi-person chore, not the assignment trio");
        ChoreHomeStats.Compute(new[] { Dto(1, DueState.NotDue, AssignmentKind.None, isSnoozed: true, requiredCount: 2) }, Me)
            .UpForGrabs.Should().Be(0, "a snoozed chore is never up for grabs");
        ChoreHomeStats.Compute(new[] { Dto(1, DueState.NotDue, AssignmentKind.None, requiredCount: 2, completedCount: 2, roster: [Me, Other]) }, Me)
            .UpForGrabs.Should().Be(1, "a finished multi-person chore falls back to the nobody-has-it rule");
    }
}
