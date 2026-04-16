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

        // LLM fallback: prefer the transcript when we have one, but fall back to the description
        // when subtitles failed to download. Many cooking videos put the full recipe in the
        // description but don't use a parseable "Instructions:" header, so the heuristic misses
        // and the LLM earns its keep.
        var llmInputText = !string.IsNullOrWhiteSpace(videoData.Transcript)
            ? videoData.Transcript
            : videoData.Description;

        if (!string.IsNullOrWhiteSpace(llmInputText))
        {
            var fromLlm = await geminiExtractor.ExtractFromTranscriptAsync(
                llmInputText,
                videoData.Title,
                videoData.Description,
                cancellationToken);

            if (fromLlm != null)
            {
                logger.LogInformation(
                    "Recipe extracted via LLM for video {VideoId} (source={Source})",
                    videoData.VideoId,
                    !string.IsNullOrWhiteSpace(videoData.Transcript) ? "transcript" : "description");
                return fromLlm;
            }
        }

        logger.LogInformation("No recipe found in video {VideoId}", videoData.VideoId);
        return null;
    }
}
