using FamilyCoordinationApp.Models.SchemaOrg;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IYouTubeRecipeExtractor
{
    /// <summary>
    /// Extracts structured recipe data from a YouTube video URL.
    /// Tries description-first, falls back to LLM transcript extraction.
    /// Returns null if no recipe can be extracted.
    /// </summary>
    Task<RecipeSchema?> ExtractRecipeAsync(string youtubeUrl, CancellationToken cancellationToken = default);
}
