using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Calendar;

/// <summary>
/// Calendar feed capabilities. Each member manages their own link (quest 8dba87f4): create, rotate and revoke touch
/// only the caller's row, so one member's rotation never breaks another member's subscription. The legacy
/// household-shared link (a row with no user) can only be read and revoked.
/// </summary>
public interface IHouseholdCalendarTokenService
{
    /// <summary>Create the member's link for <paramref name="feed"/>, revoking that member's previous one.</summary>
    Task<CreatedCalendarToken> CreateOrRotateAsync(int householdId, int userId, CalendarFeed feed, CancellationToken ct = default);

    /// <summary>Revoke the member's link for <paramref name="feed"/>. A no-op when none is active.</summary>
    Task RevokeAsync(int householdId, int userId, CalendarFeed feed, CancellationToken ct = default);

    /// <summary>Revoke the legacy household-shared link for <paramref name="feed"/>. A no-op when none is active.</summary>
    Task RevokeSharedAsync(int householdId, CalendarFeed feed, CancellationToken ct = default);

    /// <summary>
    /// The active token for a feed URL's secret, or null. A member's token resolves only while that member is an
    /// enabled user of the token's household; a legacy shared token resolves on the household alone.
    /// </summary>
    Task<HouseholdCalendarToken?> ResolveActiveAsync(string? token, CalendarFeed feed, CancellationToken ct = default);

    /// <summary>The member's own active link for <paramref name="feed"/>, or null.</summary>
    Task<HouseholdCalendarToken?> GetActiveAsync(int householdId, int userId, CalendarFeed feed, CancellationToken ct = default);

    /// <summary>The household's legacy shared link for <paramref name="feed"/>, or null.</summary>
    Task<HouseholdCalendarToken?> GetActiveSharedAsync(int householdId, CalendarFeed feed, CancellationToken ct = default);
}

public sealed record CreatedCalendarToken(string Token, DateTime CreatedAt);
