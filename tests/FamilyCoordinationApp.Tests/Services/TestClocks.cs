using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// The <see cref="IHouseholdClock"/> for tests that construct a service directly and do not care about time:
/// the real system clock in the production default zone (America/Chicago, Program.cs <c>ResolveChoresTimeZone</c>).
/// A test that pins time builds <c>new HouseholdClock(new FixedTimeProvider(...), zone)</c> instead.
/// </summary>
public static class TestClocks
{
    public static IHouseholdClock System { get; } =
        new HouseholdClock(TimeProvider.System, TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"));
}
