using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyCoordinationApp.Models.YouTube;

/// <summary>
/// POCO for deserializing the subset of yt-dlp --dump-json output that this service needs.
/// </summary>
public class YtDlpMetadata
{
    [JsonPropertyName("id")]
    public string? Id { get; set; } // Video ID — needed to locate subtitle file

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
