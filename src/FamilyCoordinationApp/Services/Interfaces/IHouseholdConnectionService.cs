using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IHouseholdConnectionService
{
    Task<HouseholdInvite> GenerateInviteAsync(int householdId, int userId, TimeSpan? validFor = null, CancellationToken cancellationToken = default);
    Task<HouseholdInvite?> GetActiveInviteAsync(int householdId, CancellationToken cancellationToken = default);
    Task InvalidateInviteAsync(int householdId, CancellationToken cancellationToken = default);
    Task<(bool IsValid, string? HouseholdName, string? Error)> ValidateInviteCodeAsync(string code, int acceptingHouseholdId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? ConnectedHouseholdName, string? Error)> AcceptInviteAsync(string code, int acceptingHouseholdId, int userId, CancellationToken cancellationToken = default);
    Task<List<ConnectedHouseholdInfo>> GetConnectedHouseholdsAsync(int householdId, CancellationToken cancellationToken = default);
    Task<bool> AreHouseholdsConnectedAsync(int id1, int id2, CancellationToken cancellationToken = default);
    Task DisconnectHouseholdsAsync(int id1, int id2, CancellationToken cancellationToken = default);
    Task<int> CleanupExpiredInvitesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// DTO for connected household info returned by GetConnectedHouseholdsAsync.
/// </summary>
public record ConnectedHouseholdInfo(int HouseholdId, string HouseholdName, DateTime ConnectedAt);
