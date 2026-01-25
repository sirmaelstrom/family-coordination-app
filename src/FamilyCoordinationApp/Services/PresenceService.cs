using System.Collections.Concurrent;

namespace FamilyCoordinationApp.Services;

public enum PresenceStatus { Online, Away, Offline }

public class UserPresence
{
    public int UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? PictureUrl { get; set; }
    public string Initials { get; set; } = string.Empty;
    public DateTime LastSeen { get; set; }
    public PresenceStatus Status { get; set; }
    public string? CurrentPage { get; set; }  // e.g., "/shopping-list"
}

public class PresenceService
{
    private readonly ConcurrentDictionary<int, UserPresence> _presence = new();

    public event Action? OnPresenceChanged;

    /// <summary>
    /// Called by components to indicate user activity.
    /// </summary>
    public void Heartbeat(int userId, string displayName, string? pictureUrl, string initials, string? currentPage = null)
    {
        _presence.AddOrUpdate(
            userId,
            new UserPresence
            {
                UserId = userId,
                DisplayName = displayName,
                PictureUrl = pictureUrl,
                Initials = initials,
                LastSeen = DateTime.UtcNow,
                Status = PresenceStatus.Online,
                CurrentPage = currentPage
            },
            (_, existing) =>
            {
                existing.LastSeen = DateTime.UtcNow;
                existing.Status = PresenceStatus.Online;
                existing.CurrentPage = currentPage;
                existing.DisplayName = displayName;
                existing.PictureUrl = pictureUrl;
                existing.Initials = initials;
                return existing;
            });

        OnPresenceChanged?.Invoke();
    }

    /// <summary>
    /// Called by polling service to update presence states based on timeouts.
    /// </summary>
    public void UpdatePresence()
    {
        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var kvp in _presence)
        {
            var timeSinceLastSeen = now - kvp.Value.LastSeen;
            var newStatus = timeSinceLastSeen switch
            {
                { TotalMinutes: > 15 } => PresenceStatus.Offline,
                { TotalMinutes: > 5 } => PresenceStatus.Away,
                _ => PresenceStatus.Online
            };

            if (kvp.Value.Status != newStatus)
            {
                kvp.Value.Status = newStatus;
                changed = true;
            }
        }

        // Remove offline users after extended period (1 hour)
        var staleUsers = _presence.Where(p => (now - p.Value.LastSeen).TotalHours > 1).Select(p => p.Key).ToList();
        foreach (var userId in staleUsers)
        {
            _presence.TryRemove(userId, out _);
            changed = true;
        }

        if (changed)
        {
            OnPresenceChanged?.Invoke();
        }
    }

    public IEnumerable<UserPresence> GetOnlineUsers() =>
        _presence.Values.Where(p => p.Status == PresenceStatus.Online);

    public IEnumerable<UserPresence> GetAllActiveUsers() =>
        _presence.Values.Where(p => p.Status != PresenceStatus.Offline);

    public IEnumerable<UserPresence> GetUsersOnPage(string page) =>
        _presence.Values.Where(p => p.Status == PresenceStatus.Online && p.CurrentPage == page);

    public UserPresence? GetUserPresence(int userId) =>
        _presence.TryGetValue(userId, out var presence) ? presence : null;

    public void UserDisconnected(int userId)
    {
        if (_presence.TryGetValue(userId, out var presence))
        {
            presence.Status = PresenceStatus.Offline;
            OnPresenceChanged?.Invoke();
        }
    }
}
