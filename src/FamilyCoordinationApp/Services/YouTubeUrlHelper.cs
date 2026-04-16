namespace FamilyCoordinationApp.Services;

public static class YouTubeUrlHelper
{
    private static readonly HashSet<string> YouTubeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "m.youtube.com", "youtu.be"
    };

    public static bool IsYouTubeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www."))
            host = host[4..];

        if (!YouTubeHosts.Contains(host))
            return false;

        if (host == "youtu.be")
            return uri.AbsolutePath.Length > 1;

        return uri.AbsolutePath.StartsWith("/watch", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase);
    }
}
