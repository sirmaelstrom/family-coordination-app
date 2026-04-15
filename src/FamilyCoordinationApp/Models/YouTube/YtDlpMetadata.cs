using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyCoordinationApp.Models.YouTube;

/// <summary>
/// Subset of yt-dlp's --dump-json output used for recipe extraction.
/// Only fields consumed by YtDlpService are mapped here.
/// </summary>
public class YtDlpMetadata
{
    [JsonPropertyName("id")]
    public string? Id { get; set; } // Video ID — used to locate the subtitle file on disk

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("subtitles")]
    public JsonElement? Subtitles { get; set; }

    [JsonPropertyName("automatic_captions")]
    public JsonElement? AutomaticCaptions { get; set; }
}
