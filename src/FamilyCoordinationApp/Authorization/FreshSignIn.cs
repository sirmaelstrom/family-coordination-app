using System.Globalization;
using System.Security.Claims;

namespace FamilyCoordinationApp.Authorization;

/// <summary>
/// When the member last proved who they are (quest 8bfa42cf). The sign-in stamps <see cref="ClaimType"/> into the
/// cookie ticket; sliding renewal keeps the claim as it was, so it records the Google sign-in, not the last request.
/// Minting an agent token needs a recent one: a stolen 30-day cookie alone must not be able to mint a year-long
/// credential that outlives a sign-out.
/// </summary>
public static class FreshSignIn
{
    public const string ClaimType = "fca:auth_time";

    /// <summary>How recent a sign-in must be to mint an agent token.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>The 403 body's machine code; the Settings page answers it with a sign-in-again prompt.</summary>
    public const string RequiredCode = "reauth_required";

    public static Claim Stamp(DateTime utcNow) =>
        new(ClaimType, new DateTimeOffset(utcNow, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    /// <summary>Whether the principal signed in within <see cref="Window"/> of <paramref name="utcNow"/>.</summary>
    public static bool IsFresh(ClaimsPrincipal principal, DateTime utcNow)
    {
        if (!long.TryParse(principal.FindFirst(ClaimType)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }
        var signedInAt = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        var age = utcNow - signedInAt;
        // A small negative age is clock skew between instances; anything more is not a real sign-in time.
        return age <= Window && age >= -TimeSpan.FromMinutes(1);
    }
}
