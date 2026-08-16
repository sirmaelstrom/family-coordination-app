using System.Security.Claims;
using FamilyCoordinationApp.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Resolves the authenticated caller's household + user id from their cookie claims (M1: the HouseholdId
/// the endpoints filter by comes ONLY from here, never from client input). <c>ShoppingListEndpoints</c>
/// still carries its own private <c>ResolveUserAsync</c> copy of this logic — a fork that predates the
/// de-Blazor flip and outlived the constraint that caused it. Folding it in is quest 9101a410.
/// </summary>
public static class UserContextResolver
{
    /// <summary>The resolved caller context: the household + user id behind the current request.</summary>
    public sealed record UserContext(int HouseholdId, int UserId);

    /// <summary>
    /// Resolve the caller's <see cref="UserContext"/> from their email claim, or <c>null</c> when there is no
    /// email claim or no matching user row. Endpoints treat <c>null</c> as <c>401 Unauthorized</c>.
    /// </summary>
    public static async Task<UserContext?> ResolveUserAsync(
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken cancellationToken)
    {
        var email = principal.FindFirst(ClaimTypes.Email)?.Value;
        if (string.IsNullOrEmpty(email)) return null;

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await context.Users
            .Where(u => u.Email == email)
            .Select(u => new { u.Id, u.HouseholdId })
            .FirstOrDefaultAsync(cancellationToken);

        return user is null ? null : new UserContext(user.HouseholdId, user.Id);
    }
}
