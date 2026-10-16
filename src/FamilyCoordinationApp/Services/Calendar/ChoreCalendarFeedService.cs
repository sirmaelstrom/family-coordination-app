using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services.Calendar;

/// <summary>Which chores a member's chores feed shows, and on which day (quest 3bdbcab7).</summary>
public interface IChoreCalendarFeedService
{
    /// <summary>
    /// The member's active chores that are assigned to them or that they claimed, each placed on its calendar day as
    /// of <paramref name="nowUtc"/> in <paramref name="timeZone"/>. Runs as the tenant in force (the anonymous feed's
    /// <c>RunAs</c> of the token's household). The caller passes the one instant it also stamps the feed with, so
    /// "today" cannot differ between the dates and the output across a midnight.
    /// </summary>
    Task<IReadOnlyList<ChoreCalendarEvent>> GetEventsAsync(int userId, DateTime nowUtc, TimeZoneInfo timeZone, CancellationToken ct = default);
}

/// <summary>
/// The day comes from <see cref="ChoreStatusCalculator.ComputeNextDueAt"/>, the same source the board's dueness
/// reads, so the calendar never disagrees with the app:
/// <list type="bullet">
/// <item>Flexible: last completion + interval (today when never completed), so the event slides as completions
/// shift it (operator answer (a), receipt 96548028).</item>
/// <item>Fixed: the current or next slot, completion-aware. OneOff: its anchor, none once done.</item>
/// <item>The snooze floor applies to all three, including a recurrence with no natural next-due (the calculator then
/// answers the snooze date).</item>
/// </list>
/// A next-due before today is overdue: the event sits on today as "Overdue", since a calendar shows nothing on a
/// missed past date that anyone looks at. A chore whose final next-due is null (a satisfied one-off, an unsnoozed
/// under-specified recurrence) has no event. A stale claim (past the 48-hour auto-release threshold, not yet
/// released) is still the member's claim in the data, so it stays in the feed until the release runs.
/// </summary>
public sealed class ChoreCalendarFeedService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ChoreStatusCalculator calculator) : IChoreCalendarFeedService
{
    public async Task<IReadOnlyList<ChoreCalendarEvent>> GetEventsAsync(
        int userId, DateTime nowUtc, TimeZoneInfo timeZone, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var chores = await context.Chores
            .AsNoTracking()
            .Where(chore => chore.AssigneeUserId == userId
                && (chore.AssignmentKind == AssignmentKind.Assigned || chore.AssignmentKind == AssignmentKind.Claimed)
                && chore.Status == ChoreStatus.Active)
            .ToListAsync(ct);

        var today = ChoreStatusCalculator.LocalDate(nowUtc, timeZone);
        var events = new List<ChoreCalendarEvent>(chores.Count);
        foreach (var chore in chores)
        {
            var nextDueAt = calculator.ComputeNextDueAt(ChoreRecurrenceSnapshot.FromChore(chore), nowUtc, timeZone);
            if (nextDueAt is not { } due) continue;

            var dueDate = ChoreStatusCalculator.LocalDate(due, timeZone);
            var overdue = dueDate < today;
            events.Add(new ChoreCalendarEvent(
                chore.ChoreId,
                chore.Name,
                chore.Description,
                overdue ? today : dueDate,
                overdue,
                chore.AssignmentKind));
        }

        return events;
    }
}
