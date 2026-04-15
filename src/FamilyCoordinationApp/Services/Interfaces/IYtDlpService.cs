namespace FamilyCoordinationApp.Services.Interfaces;

public interface IYtDlpService
{
    /// <summary>
    /// Extracts video metadata (title, description, duration, channel) and optional transcript
    /// from a YouTube URL using yt-dlp subprocess.
    /// Returns null if yt-dlp fails or the URL is invalid.
    /// </summary>
    Task<Models.YouTube.YouTubeVideoData?> ExtractVideoDataAsync(string youtubeUrl, CancellationToken cancellationToken = default);
}
