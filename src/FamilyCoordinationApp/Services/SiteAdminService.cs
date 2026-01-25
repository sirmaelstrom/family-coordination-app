namespace FamilyCoordinationApp.Services;

/// <summary>
/// Service to check if a user is a site admin based on environment configuration.
/// Site admins have elevated privileges like viewing all feedback across households.
/// </summary>
public interface ISiteAdminService
{
    /// <summary>
    /// Check if the given email is a site admin.
    /// </summary>
    bool IsSiteAdmin(string? email);
}

public class SiteAdminService(IConfiguration configuration) : ISiteAdminService
{
    private readonly HashSet<string> _adminEmails = (configuration["SITE_ADMIN_EMAILS"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(e => e.ToLowerInvariant())
        .ToHashSet();

    public bool IsSiteAdmin(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        return _adminEmails.Contains(email.ToLowerInvariant());
    }
}
