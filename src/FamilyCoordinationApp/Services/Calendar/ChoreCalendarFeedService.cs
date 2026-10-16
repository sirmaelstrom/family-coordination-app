using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services.Calendar;

/// <summary>Which chores a member's chores feed shows, and on which day (quest 3bdbcab7).</summary>
public interface IChoreCalendarFeedService
{
    /// <summary>
    /// The member's active chores that are assigned to them or that they claimed, each placed on its calendar day.
    /// Runs as the tenant in force (the anonymous feed's <c>RunAs</c> of the token's household).
    /// </summary>
    Task<IReadOnlyList<ChoreCalendarEvent>> GetEventsAsync(int householdId, int userId, CancellationToken ct = default);
}

/// <summary>
/// The day comes from <see cref="ChoreStatusCalculator.ComputeNextDueAt"/>, the same source the board's dueness
/// reads, so the calendar never disagrees with the app:
/// <list type="bullet">
/// <item>Flexible: last completion + interval (today when never completed), so the event slides as completions
/// shift it (operator answer (a), receipt 96548028).</item>
/// <item>Fixed: the current or next slot, completion-aware. OneOff: its anchor, none once done.</item>
/// <item>The snooze floor applies to all three.</item>
/// </list>
/// A next-due before today is overdue: the event sits on today as "Overdue", since a calendar shows nothing on a
/// missed past date that anyone looks at. A chore with no next-due (a satisfied one-off, an under-specified
/// recurrence) has no event.
/// </summary>
public sealed class ChoreCalendarFeedService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ChoreStatusCalculator calculator,
    IHouseholdClock clock) : IChoreCalendarFeedService
{
    public async Task<IReadOnlyList<ChoreCalendarEvent>> GetEventsAsync(int householdId, int userId, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var chores = await context.Chores
            .AsNoTracking()
            .Where(chore => chore.AssigneeUserId == userId
                && (chore.AssignmentKind == AssignmentKind.Assigned || chore.AssignmentKind == AssignmentKind.Claimed)
                && chore.Status == ChoreStatus.Active)
            .ToListAsync(ct);

        var now = clock.UtcNow;
        var tz = clock.TimeZone(householdId);
        var today = clock.Today(householdId);
        var events = new List<ChoreCalendarEvent>(chores.Count);
        foreach (var chore in chores)
        {
            var nextDueAt = calculator.ComputeNextDueAt(ChoreRecurrenceSnapshot.FromChore(chore), now, tz);
            if (nextDueAt is not { } due) continue;

            var dueDate = ChoreStatusCalculator.LocalDate(due, tz);
            var overdue = dueDate < today;
            events.Add(new ChoreCalendarEvent(
                chore.ChoreId,
                chore.Name,
                chore.Description,
                overdue ? today : dueDate,
                overdue,
                chore.AssignmentKind,
                chore.Version));
        }

        return events;
    }
}
