namespace FamilyCoordinationApp.Services;

public static class YouTubeUrlHelper
{
    private static readonly HashSet<string> YouTubeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "youtu.be",
        "www.youtu.be"
    };

    private static readonly HashSet<string> ShortLinkHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtu.be", "www.youtu.be"
    };

    public static bool IsYouTubeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host;
        if (!YouTubeHosts.Contains(host))
            return false;

        if (ShortLinkHosts.Contains(host))
            return uri.AbsolutePath.Length > 1;

        return uri.AbsolutePath.StartsWith("/watch", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/embed/", StringComparison.OrdinalIgnoreCase);
    }
}
