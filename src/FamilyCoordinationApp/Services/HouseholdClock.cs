namespace FamilyCoordinationApp.Services;

/// <summary>
/// The one source of "now" and of a household's "today" (quest 5197d71c). Server code reads time through this
/// rather than <see cref="DateTime.UtcNow"/> or <see cref="DateTime.Today"/>; <c>ClockArchitectureTests</c> fails on
/// a direct read outside the allowlist it carries.
/// <see cref="DateTime.Today"/> is the server process's local date, which differs from the household's date for
/// part of every day, so the dashboard and meal plan used to disagree with chores around local midnight.
/// </summary>
public interface IHouseholdClock
{
    /// <summary>The current UTC instant (<see cref="DateTimeKind.Utc"/>).</summary>
    DateTime UtcNow { get; }

    /// <summary>The household's current local calendar date.</summary>
    DateOnly Today(int householdId);

    /// <summary>The household's timezone, for day-boundary math that needs more than today's date.</summary>
    TimeZoneInfo TimeZone(int householdId);
}

/// <summary>
/// <see cref="IHouseholdClock"/> over the injected <see cref="TimeProvider"/> and the household timezone. The zone
/// is process-global today (<c>CHORES_TIMEZONE</c>, resolved in Program.cs), so <c>householdId</c> does not select
/// it yet; the parameter is the seam for per-household zones.
/// </summary>
public sealed class HouseholdClock(TimeProvider timeProvider, TimeZoneInfo timeZone) : IHouseholdClock
{
    public DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public DateOnly Today(int householdId) => ChoreStatusCalculator.LocalDate(UtcNow, timeZone);

    public TimeZoneInfo TimeZone(int householdId) => timeZone;
}
