namespace FamilyCoordinationApp.Services.Interfaces;

public interface IRecipeImportService
{
    /// <summary>
    /// Imports recipe from URL, returning Recipe entity or error with partial data.
    /// </summary>
    Task<RecipeImportResult> ImportFromUrlAsync(string url, int householdId, int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate an already-extracted schema.org recipe and convert it to an unsaved recipe (no fetch, no model call).
    /// </summary>
    RecipeImportResult FromSchema(Models.SchemaOrg.RecipeSchema? schema, string url, int householdId, int userId);
}
