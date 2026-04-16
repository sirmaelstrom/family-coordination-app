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
                logger.LogInformation("Recipe extracted from description for video {VideoId}", videoData.VideoId);
                return fromDescription;
            }
        }

        if (!string.IsNullOrWhiteSpace(videoData.Transcript))
        {
            var fromTranscript = await geminiExtractor.ExtractFromTranscriptAsync(
                videoData.Transcript,
                videoData.Title,
                videoData.Description,
                cancellationToken);

            if (fromTranscript != null)
            {
                logger.LogInformation("Recipe extracted from transcript via LLM for video {VideoId}", videoData.VideoId);
                return fromTranscript;
            }
        }

        logger.LogInformation("No recipe found in video {VideoId}", videoData.VideoId);
        return null;
    }
}
