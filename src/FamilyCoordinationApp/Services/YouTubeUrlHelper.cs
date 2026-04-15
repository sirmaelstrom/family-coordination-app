namespace FamilyCoordinationApp.Services;

/// <summary>
/// Helper for detecting YouTube URLs.
/// Single source of truth for YouTube URL matching across the application.
/// </summary>
public static class YouTubeUrlHelper
{
    private static readonly string[] YouTubeDomains =
    [
        "youtube.com",
        "www.youtube.com",
        "m.youtube.com",
        "youtu.be",
    ];

    /// <summary>
    /// Returns true if the given URL points to a YouTube video or playlist.
    /// </summary>
    public static bool IsYouTubeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        return Array.Exists(YouTubeDomains, d => d == host);
    }
}
