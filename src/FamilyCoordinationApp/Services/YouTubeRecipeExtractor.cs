using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class YouTubeRecipeExtractor(
    IYtDlpService ytDlpService,
    IDescriptionRecipeExtractor descriptionExtractor,
    IGeminiRecipeExtractor geminiExtractor,
    ILogger<YouTubeRecipeExtractor> logger) : IYouTubeRecipeExtractor
{
    public async Task<RecipeSchema?> ExtractRecipeAsync(string youtubeUrl, CancellationToken cancellationToken = default)
    {
        // Step 1: Extract video metadata and optional transcript via yt-dlp
        var videoData = await ytDlpService.ExtractVideoDataAsync(youtubeUrl, cancellationToken);
        if (videoData == null)
        {
            logger.LogWarning("yt-dlp returned no data for {Url}", youtubeUrl);
            return null;
        }

        // Step 2: Try description-first extraction (no LLM cost)
        if (!string.IsNullOrWhiteSpace(videoData.Description))
        {
            var descriptionSchema = descriptionExtractor.ExtractFromDescription(videoData.Description, videoData.Title);
            if (descriptionSchema != null)
            {
                logger.LogInformation("Recipe extracted from description for {Url}", youtubeUrl);
                return descriptionSchema;
            }
        }

        // Step 3: Fall back to LLM transcript extraction if transcript is available
        if (!string.IsNullOrWhiteSpace(videoData.Transcript))
        {
            var transcriptSchema = await geminiExtractor.ExtractFromTranscriptAsync(
                videoData.Transcript,
                videoData.Title,
                videoData.Description,
                cancellationToken);

            if (transcriptSchema != null)
            {
                logger.LogInformation("Recipe extracted from transcript via LLM for {Url}", youtubeUrl);
                return transcriptSchema;
            }
        }

        logger.LogInformation("No recipe found in video {Url}", youtubeUrl);
        return null;
    }
}
