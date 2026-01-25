namespace FamilyCoordinationApp.Services.Interfaces;

public interface IRecipeImportService
{
    /// <summary>
    /// Imports recipe from URL, returning Recipe entity or error with partial data.
    /// </summary>
    Task<RecipeImportResult> ImportFromUrlAsync(string url, int householdId, int userId, CancellationToken cancellationToken = default);
}
