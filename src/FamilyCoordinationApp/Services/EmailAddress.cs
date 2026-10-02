using System.Security.Claims;

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
}
