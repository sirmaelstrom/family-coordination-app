using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Models.YouTube;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class YouTubeRecipeExtractor(
    IYtDlpService ytDlpService,
    IDescriptionRecipeExtractor descriptionExtractor,
    IGeminiRecipeExtractor geminiExtractor,
    ILogger<YouTubeRecipeExtractor> logger) : IYouTubeRecipeExtractor
{
    internal const int MaxStampedDescriptionChars = 500;

    public async Task<RecipeSchema?> ExtractRecipeAsync(string youtubeUrl, CancellationToken cancellationToken = default)
    {
        var videoData = await ytDlpService.ExtractVideoDataAsync(youtubeUrl, cancellationToken);
        if (videoData == null)
        {
            logger.LogWarning("yt-dlp returned no data for {Url}", youtubeUrl);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(videoData.Description))
        {
            var fromDescription = descriptionExtractor.ExtractFromDescription(videoData.Description, videoData.Title);
            if (fromDescription != null)
            {
                StampVideoMetadata(fromDescription, videoData);
                logger.LogInformation("Recipe extracted from description for video {VideoId}", videoData.VideoId);
                return fromDescription;
            }
        }

        // LLM fallback: prefer the transcript when we have one, but fall back to the description
        // when subtitles failed to download. Many cooking videos put the full recipe in the
        // description but don't use a parseable "Instructions:" header, so the heuristic misses
        // and the LLM earns its keep.
        var llmInputText = !string.IsNullOrWhiteSpace(videoData.Transcript)
            ? videoData.Transcript
            : videoData.Description;

        if (!string.IsNullOrWhiteSpace(llmInputText))
        {
            var source = !string.IsNullOrWhiteSpace(videoData.Transcript) ? "transcript" : "description";
            logger.LogInformation(
                "Sending {Source} to Gemini for {VideoId} ({Length} chars)",
                source, videoData.VideoId, llmInputText.Length);

            var fromLlm = await geminiExtractor.ExtractFromTranscriptAsync(
                llmInputText,
                videoData.Title,
                videoData.Description,
                cancellationToken);

            if (fromLlm != null)
            {
                StampVideoMetadata(fromLlm, videoData);
                logger.LogInformation(
                    "Recipe extracted via LLM for video {VideoId} (source={Source})",
                    videoData.VideoId, source);
                return fromLlm;
            }
        }

        logger.LogInformation("No recipe found in video {VideoId}", videoData.VideoId);
        return null;
    }

    // Populate Image/Description from video metadata when the extractor didn't supply them.
    // YouTube thumbnails and descriptions are lower-quality than blog JSON-LD, but beat null
    // on the edit page.
    internal static void StampVideoMetadata(RecipeSchema schema, YouTubeVideoData videoData)
    {
        if (schema.Image == null && !string.IsNullOrWhiteSpace(videoData.ThumbnailUrl))
            schema.Image = videoData.ThumbnailUrl;

        if (string.IsNullOrWhiteSpace(schema.Description) && !string.IsNullOrWhiteSpace(videoData.Description))
            schema.Description = Truncate(videoData.Description, MaxStampedDescriptionChars);
    }

    private static string Truncate(string input, int max)
    {
        if (input.Length <= max) return input;
        return input[..max].TrimEnd() + "…";
    }
}
