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
    private static ChoreDto Dto(int id, DueState dueState, AssignmentKind kind, bool isSnoozed = false, bool isClaimStale = false) => new(
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
        RequiredCount: 1,
        CompletedCount: 0,
        Roster: [],
        Subtasks: []);

    [Fact]
    public void Compute_ExcludesSnoozedFromUpForGrabs_WithClearedControl()
    {
        // The same unclaimed chore: snoozed → NOT up-for-grabs; un-snoozed → up-for-grabs.
        ChoreHomeStats.Compute(new[] { Dto(1, DueState.Scheduled, AssignmentKind.None, isSnoozed: true) })
            .UpForGrabs.Should().Be(0);

        ChoreHomeStats.Compute(new[] { Dto(1, DueState.Scheduled, AssignmentKind.None, isSnoozed: false) })
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

        var r = ChoreHomeStats.Compute(chores);

        r.Total.Should().Be(5);
        r.Overdue.Should().Be(1, "snoozed #5 reads Scheduled — it never inflates Overdue");
        r.DueToday.Should().Be(1, "snoozed #5 reads Scheduled — it never inflates DueToday");
        r.UpForGrabs.Should().Be(3, "#2, #3, #4 — NOT the snoozed #5 nor the assigned #1");
    }
}
