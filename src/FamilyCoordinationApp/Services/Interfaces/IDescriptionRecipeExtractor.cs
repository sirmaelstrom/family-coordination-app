using FamilyCoordinationApp.Models.SchemaOrg;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IDescriptionRecipeExtractor
{
    /// <summary>
    /// Attempts to extract a structured recipe from a video description string.
    /// Returns null if no recipe pattern is detected.
    /// </summary>
    /// <param name="description">The video description text</param>
    /// <param name="videoTitle">The video title, used as fallback recipe name</param>
    RecipeSchema? ExtractFromDescription(string description, string? videoTitle = null);
}
