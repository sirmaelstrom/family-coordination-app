using FamilyCoordinationApp.Models.SchemaOrg;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IGeminiRecipeExtractor
{
    /// <summary>
    /// Extracts structured recipe data from transcript text using Gemini Flash.
    /// Returns null if extraction fails or no recipe is found.
    /// </summary>
    Task<RecipeSchema?> ExtractFromTranscriptAsync(
        string transcriptText,
        string? videoTitle = null,
        string? videoDescription = null,
        CancellationToken cancellationToken = default);
}
