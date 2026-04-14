namespace FamilyCoordinationApp.Models.YouTube;

/// <summary>
/// Extracted YouTube video data — metadata plus optional transcript.
/// Returned by IYtDlpService. Property names are contract — WP-04 references them.
/// </summary>
public class YouTubeVideoData
{
    public required string VideoId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Channel { get; init; }

    /// <summary>
    /// Full transcript text concatenated from subtitle segments.
    /// Null if no captions are available for the video.
    /// </summary>
    public string? Transcript { get; init; }
}
