using System.Security.Cryptography;
using System.Text;

namespace FamilyCoordinationApp.Services.AgentAccess;

/// <summary>
/// The shape of a personal access token: <c>fca_pat_</c> + 43 base64url characters (32 random bytes). The prefix
/// makes a leaked token recognisable to secret scanners and to a human; the 256-bit body is why a plain SHA-256 at
/// rest is enough (it is not a password, so a slow hash buys nothing).
/// </summary>
public static class AgentTokenFormat
{
    public const string Prefix = "fca_pat_";
    private const int RandomBytes = 32;

    /// <summary><see cref="Prefix"/> (8) + the base64url body of <see cref="RandomBytes"/> without padding (43).</summary>
    private const int TokenLength = 51;

    /// <summary>How many characters of the secret the app keeps and shows (the prefix plus four).</summary>
    private const int SafePrefixLength = 12;

    /// <summary>A fresh secret. The caller shows it once and stores only <see cref="Hash"/>.</summary>
    public static string Create()
    {
        var body = Convert.ToBase64String(RandomNumberGenerator.GetBytes(RandomBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Prefix + body;
    }

    /// <summary>Whether <paramref name="token"/> could be a token at all, checked before any lookup.</summary>
    public static bool IsWellFormed(string? token) =>
        token is { Length: TokenLength }
        && token.StartsWith(Prefix, StringComparison.Ordinal)
        && token[Prefix.Length..].All(IsBase64UrlCharacter);

    /// <summary>Uppercase hex SHA-256 of the whole token, the stored lookup key.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string SafePrefixOf(string token) => token[..SafePrefixLength];

    private static bool IsBase64UrlCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is '-' or '_';
}
