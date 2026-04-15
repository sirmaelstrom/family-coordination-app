namespace FamilyCoordinationApp.Services;

public static class YouTubeUrlHelper
{
    private static readonly HashSet<string> YouTubeHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "m.youtube.com", "youtu.be"
    };

    public static bool IsYouTubeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www."))
            host = host[4..];

        if (!YouTubeHosts.Contains(host))
            return false;

        // youtu.be short links: path IS the video ID (e.g., youtu.be/dQw4w9WgXcQ)
        if (host == "youtu.be")
            return uri.AbsolutePath.Length > 1; // must have a video ID after "/"

        // youtube.com variants: must be a video-like path
        return uri.AbsolutePath.StartsWith("/watch")
            || uri.AbsolutePath.StartsWith("/shorts/")
            || uri.AbsolutePath.StartsWith("/embed/");
    }
}
