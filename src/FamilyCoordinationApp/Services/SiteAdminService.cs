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

public class SiteAdminService : ISiteAdminService
{
    private readonly HashSet<string> _adminEmails;

    public SiteAdminService(IConfiguration configuration)
    {
        // Read comma-separated list of admin emails from environment variable
        var adminEmailsConfig = configuration["SITE_ADMIN_EMAILS"] ?? "";
        
        _adminEmails = adminEmailsConfig
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant())
            .ToHashSet();
    }

    public bool IsSiteAdmin(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        return _adminEmails.Contains(email.ToLowerInvariant());
    }
}
