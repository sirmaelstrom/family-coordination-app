using System.Security.Claims;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

public static class EmailAddress
{
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    public static void NormalizeClaims(ClaimsPrincipal principal)
    {
        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.FindAll(ClaimTypes.Email).ToArray())
            {
                var normalized = new Claim(claim.Type, Normalize(claim.Value), claim.ValueType,
                    claim.Issuer, claim.OriginalIssuer, identity);
                foreach (var property in claim.Properties)
                    normalized.Properties.Add(property);
                identity.RemoveClaim(claim);
                identity.AddClaim(normalized);
            }
        }
    }

    // Stored emails are matched case-insensitively, not by equality with the normalized value: rows written before
    // quest 3642dbb8 may hold mixed case (the claim-based writers stored the claim as sent), and no migration has
    // rewritten them. Npgsql translates the predicate to lower(btrim("Email", E' \t\n\r')) = @normalized, so the
    // case-sensitive Email index does not serve these lookups.

    /// <summary>Users whose stored email equals <paramref name="email"/> after normalizing both sides.</summary>
    public static IQueryable<User> WhereEmailMatches(this IQueryable<User> users, string email)
    {
        var normalized = Normalize(email);
        return users.Where(u => u.Email.Trim().ToLower() == normalized);
    }

    /// <summary>Household requests whose stored email equals <paramref name="email"/> after normalizing both sides.</summary>
    public static IQueryable<HouseholdRequest> WhereEmailMatches(this IQueryable<HouseholdRequest> requests, string email)
    {
        var normalized = Normalize(email);
        return requests.Where(r => r.Email.Trim().ToLower() == normalized);
    }

    /// <summary>
    /// The one account an identity lookup resolves to, or <c>null</c>. When two rows differ only in case (legacy
    /// case-duplicates, possibly in different households), it refuses and logs a warning instead of picking one:
    /// either pick could put the caller in the wrong household.
    /// </summary>
    public static async Task<T?> SingleIdentityOrDefaultAsync<T>(
        this IQueryable<T> matches, ILogger logger, string email, CancellationToken cancellationToken = default)
        where T : class
    {
        var rows = await matches.Take(2).ToListAsync(cancellationToken);
        if (rows.Count < 2)
            return rows.FirstOrDefault();

        logger.LogWarning(
            "Email {Email} matches more than one user when case is ignored; refusing to resolve an identity until " +
            "the rows are reconciled", Normalize(email));
        return null;
    }

    /// <summary>
    /// The caller's newest household request, or <c>null</c>. When several match (legacy rows differing in case),
    /// the newest wins and a warning is logged.
    /// </summary>
    public static async Task<HouseholdRequest?> NewestRequestOrDefaultAsync(
        this IQueryable<HouseholdRequest> matches, ILogger logger, string email,
        CancellationToken cancellationToken = default)
    {
        var rows = await matches
            .OrderByDescending(r => r.RequestedAt)
            .ThenByDescending(r => r.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (rows.Count > 1)
        {
            logger.LogWarning(
                "Email {Email} matches more than one household request when case is ignored; using the newest, {RequestId}",
                Normalize(email), rows[0].Id);
        }

        return rows.FirstOrDefault();
    }
}
