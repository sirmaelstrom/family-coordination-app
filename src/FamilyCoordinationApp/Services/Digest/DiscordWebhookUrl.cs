namespace FamilyCoordinationApp.Services.Digest;

/// <summary>
/// The one rule for which URLs the digest may POST to (quest e9c2e497). Any household member can set the webhook, and
/// the server POSTs to it, so an unchecked URL lets a member point the server at internal hosts (localhost, the
/// docker network, the LAN). Only a Discord incoming-webhook URL is accepted: <c>https</c>, default port, no
/// credentials, a Discord host, and a path under <c>/api/webhooks/</c>. The rule runs at write time
/// (<c>DigestSettingsService.ValidateUpdate</c>, a 400) and again at send time (<c>DiscordWebhookDigestSender</c>), so
/// a value stored before the rule existed is refused too. The named HTTP client does not follow redirects, so an
/// accepted host cannot bounce the request elsewhere.
/// </summary>
public static class DiscordWebhookUrl
{
    /// <summary>The hosts Discord serves incoming webhooks on.</summary>
    public static readonly IReadOnlyList<string> AllowedHosts =
        ["discord.com", "discordapp.com", "canary.discord.com", "ptb.discord.com"];

    /// <summary>The longest URL accepted. Real webhook URLs are about 120 characters; the protected value of 500
    /// characters fits the 2000-character column with room to spare (measured in the tests).</summary>
    public const int MaxLength = 500;

    /// <summary>Why <paramref name="url"/> is refused, or <c>null</c> when it is an acceptable webhook URL. The reason
    /// never echoes the URL (it is a secret).</summary>
    public static string? Problem(string url)
    {
        if (url.Length > MaxLength) return $"The webhook URL must be {MaxLength} characters or fewer.";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "The webhook URL is not a valid URL.";
        if (uri.Scheme != Uri.UriSchemeHttps) return "The webhook URL must start with https://.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "The webhook URL must not contain a user name or password.";
        if (!uri.IsDefaultPort) return "The webhook URL must not name a port.";
        if (!AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
        {
            return "The webhook URL must be a Discord webhook (discord.com).";
        }
        if (!uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal))
        {
            return "The webhook URL must be a Discord webhook URL (…/api/webhooks/…).";
        }
        return null;
    }
}
