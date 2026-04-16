using FamilyCoordinationApp.Models.SchemaOrg;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IDescriptionRecipeExtractor
{
    /// <summary>
    /// Attempts to extract a structured recipe from a video description string.
    /// Returns null if no recipe pattern is detected.
    /// </summary>
    RecipeSchema? ExtractFromDescription(string? description, string? videoTitle = null);
}
