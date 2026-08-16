using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Copies the Google profile claims onto the persisted <c>User</c> row at sign-in. Wired to the OAuth
/// <c>OnCreatingTicket</c> event in <c>Program.cs</c>, which fires once per sign-in — this write previously lived
/// in <c>WhitelistedEmailHandler</c> and therefore ran on every authorization evaluation of every request.
/// </summary>
/// <remarks>
/// <para>REFRESH HORIZON: a fresh OAuth ticket, and nothing else. The cookie is 30 days with sliding expiration,
/// so a continuously active user may not produce another ticket for months — or ever. Google-derived data here is
/// therefore best-effort, never authoritative.</para>
/// <para>OWNERSHIP: <c>DisplayName</c> is NOT Google-owned. It is set at creation (email local-part for invited
/// and admin-added users, the operator's text for an approved request) and no path updates it from Google, so
/// initials are derived from the stored <c>DisplayName</c> rather than the name claim — otherwise a user shown as
/// "bob" renders the initials of their Google name.</para>
/// </remarks>
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
            user.Initials = UserProfile.ComputeInitials(user.DisplayName);

            // Only overwrite with something. An account with no Google photo sends no picture claim, and
            // assigning it unconditionally would erase an avatar the user already has.
            var picture = principal.FindFirst("urn:google:picture")?.Value;
            if (!string.IsNullOrWhiteSpace(picture))
                user.PictureUrl = picture;

            // First Google sign-in for a user the admin created or an invite produced: their row was written with
            // GoogleId = null, and this is the only place that can fill it. Never reassigned — a non-null value
            // is that account's identity, and the column carries a filtered unique index.
            if (string.IsNullOrWhiteSpace(user.GoogleId))
            {
                var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrWhiteSpace(subject))
                    user.GoogleId = subject;
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // A profile refresh must never fail the sign-in it hangs off.
            logger.LogError(ex, "Failed to refresh the login profile for {Email}", email);
        }
    }
}
