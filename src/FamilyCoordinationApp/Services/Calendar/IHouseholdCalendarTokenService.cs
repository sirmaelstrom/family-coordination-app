using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Calendar;

public interface IHouseholdCalendarTokenService
{
    Task<CreatedCalendarToken> CreateOrRotateAsync(int householdId, CancellationToken ct = default);
    Task RevokeAsync(int householdId, CancellationToken ct = default);
    Task<HouseholdCalendarToken?> ResolveActiveAsync(string? token, CancellationToken ct = default);
    Task<HouseholdCalendarToken?> GetActiveAsync(int householdId, CancellationToken ct = default);
}

public sealed record CreatedCalendarToken(string Token, DateTime CreatedAt);
