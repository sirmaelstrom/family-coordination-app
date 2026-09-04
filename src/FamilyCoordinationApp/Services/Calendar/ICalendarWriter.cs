using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services.Calendar;

public interface ICalendarWriter
{
    string WriteMealPlan(int householdId, DateOnly windowStart, IEnumerable<MealPlanEntryDto> entries, DateTimeOffset nowUtc);
}
