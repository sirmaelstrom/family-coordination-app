using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services.Calendar;

public interface ICalendarWriter
{
    string WriteMealPlan(int householdId, DateOnly windowStart, IEnumerable<MealPlanEntryDto> entries, DateTimeOffset nowUtc);

    /// <summary>
    /// A member's chores feed (quest 3bdbcab7): one all-day event per chore on its calendar day. An empty set
    /// writes one placeholder event on <paramref name="today"/>.
    /// </summary>
    string WriteChores(int householdId, int userId, DateOnly today, IEnumerable<ChoreCalendarEvent> chores, DateTimeOffset nowUtc);
}

/// <summary>
/// One chore as the calendar shows it: on <see cref="Date"/> (its next-due local date, or today when it is
/// overdue, in which case <see cref="Overdue"/> is set).
/// </summary>
public sealed record ChoreCalendarEvent(
    int ChoreId,
    string Name,
    string? Description,
    DateOnly Date,
    bool Overdue,
    AssignmentKind AssignmentKind,
    uint Version);
