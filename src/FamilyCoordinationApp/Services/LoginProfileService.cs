using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Copies the Google profile claims onto the persisted <c>User</c> row at sign-in. Wired to the OAuth
/// <c>OnCreatingTicket</c> event in <c>Program.cs</c>, which fires once per sign-in — this write previously lived
/// in <c>WhitelistedEmailHandler</c> and therefore ran on every authorization evaluation of every request.
/// </summary>
public sealed class LoginProfileService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TimeProvider clock,
    ILogger<LoginProfileService> logger)
{
    /// <summary>
    /// No-op when the principal carries no email claim or no <c>User</c> row matches it, so a sign-in that
    /// precedes the account (first-run setup, a pending household request) still succeeds. Unlike the handler it
    /// replaces it does not require <c>IsWhitelisted</c>: a login happened either way.
    /// </summary>
    public async Task RefreshAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var email = principal.FindFirst(ClaimTypes.Email)?.Value;
        if (string.IsNullOrWhiteSpace(email))
            return;

        try
        {
            await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
            var user = await context.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
            if (user is null)
                return;

            user.LastLoginAt = clock.GetUtcNow().UtcDateTime;
            user.PictureUrl = principal.FindFirst("urn:google:picture")?.Value;
            user.Initials = UserProfile.ComputeInitials(
                principal.FindFirst(ClaimTypes.Name)?.Value ?? user.DisplayName);

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // A profile refresh must never fail the sign-in it hangs off.
            logger.LogError(ex, "Failed to refresh the login profile for {Email}", email);
        }
    }
}
