namespace FamilyCoordinationApp.Services;

/// <summary>
/// Derivations of the persisted <c>User</c> display fields, shared by the paths that create a user and the
/// login-time refresh. Every <c>User</c> row must be born with its <c>Initials</c> already set: nothing else
/// backfills them, and the avatar surfaces (member lists, presence, recipe authorship) read the column directly.
/// </summary>
public static class UserProfile
{
    /// <summary>First + last initial, uppercased; "?" when no usable display name.</summary>
    public static string ComputeInitials(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "?";

        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "?";
        if (parts.Length == 1)
            return parts[0][0].ToString().ToUpperInvariant();

        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }
}
