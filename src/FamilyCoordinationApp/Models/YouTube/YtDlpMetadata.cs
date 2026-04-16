using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyCoordinationApp.Models.YouTube;

/// <summary>
/// Subset of yt-dlp --dump-json output fields used for recipe extraction.
/// </summary>
public class YtDlpMetadata
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

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
