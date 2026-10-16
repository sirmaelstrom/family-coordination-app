using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;
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
        // Single-person chores: the assignment trio names the member.
        var assigned = await context.Chores
            .AsNoTracking()
            .Where(chore => chore.AssigneeUserId == userId
                && (chore.AssignmentKind == AssignmentKind.Assigned || chore.AssignmentKind == AssignmentKind.Claimed)
                && chore.Status == ChoreStatus.Active)
            .ToListAsync(ct);
        var mine = assigned
            .Select(chore => (Chore: chore, Role: chore.AssignmentKind == AssignmentKind.Claimed ? ChoreFeedRole.Claimed : ChoreFeedRole.Assigned))
            .ToList();

        // Multi-person chores (RequiredCount > 1) keep no assignee: their roster is folded on read from the
        // participation events and the completion ledger, exactly as the board does (ChoreRosterCalculator). The
        // member is on it while Assigned or In; Done means their part of this occurrence is finished.
        var multiPerson = await context.Chores
            .AsNoTracking()
            .Where(chore => chore.RequiredCount > 1 && chore.Status == ChoreStatus.Active)
            .ToListAsync(ct);
        if (multiPerson.Count > 0)
        {
            var ids = multiPerson.Select(chore => chore.ChoreId).ToList();
            var participation = (await context.ChoreParticipationEvents.AsNoTracking().Where(e => ids.Contains(e.ChoreId)).ToListAsync(ct))
                .ToLookup(e => e.ChoreId);
            var completions = (await context.ChoreCompletions.AsNoTracking().Where(c => ids.Contains(c.ChoreId)).ToListAsync(ct))
                .ToLookup(c => c.ChoreId);
            foreach (var chore in multiPerson)
            {
                var roster = ChoreRosterCalculator.Fold(
                    participation[chore.ChoreId],
                    completions[chore.ChoreId],
                    chore.LastCompletedAt,
                    chore.RequiredCount,
                    recurring: chore.RecurrenceMode != RecurrenceMode.OneOff);
                if (roster.Members.Any(m => m.UserId == userId && m.State is RosterState.Assigned or RosterState.In)
                    && mine.All(m => m.Chore.ChoreId != chore.ChoreId))
                {
                    mine.Add((chore, ChoreFeedRole.Roster));
                }
            }
        }

        var today = ChoreStatusCalculator.LocalDate(nowUtc, timeZone);
        var events = new List<ChoreCalendarEvent>(mine.Count);
        foreach (var (chore, role) in mine)
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
                role));
        }

        return events;
    }
}
