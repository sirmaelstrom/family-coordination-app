using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

public record RecipeDraftData(
    string Name,
    string? Description,
    string? Instructions,
    string? ImagePath,
    string? SourceUrl,
    int? Servings,
    int? PrepTimeMinutes,
    int? CookTimeMinutes,
    List<IngredientDraftData> Ingredients
);

public record IngredientDraftData(
    string Name,
    decimal? Quantity,
    string? Unit,
    string Category,
    string? Notes,
    string? GroupName,
    int SortOrder
);

public interface IDraftService
{
    Task SaveDraftAsync(int householdId, int userId, int? recipeId, RecipeDraftData draft, CancellationToken cancellationToken = default);
    Task<RecipeDraftData?> GetDraftAsync(int householdId, int userId, int? recipeId, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(int householdId, int userId, int? recipeId, CancellationToken cancellationToken = default);
}

public class DraftService : IDraftService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<DraftService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DraftService(IDbContextFactory<ApplicationDbContext> dbFactory, ILogger<DraftService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task SaveDraftAsync(int householdId, int userId, int? recipeId, RecipeDraftData draft, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.RecipeDrafts
            .FirstOrDefaultAsync(d =>
                d.HouseholdId == householdId &&
                d.UserId == userId &&
                d.RecipeId == recipeId,
                cancellationToken);

        var json = JsonSerializer.Serialize(draft, JsonOptions);

        if (existing != null)
        {
            existing.DraftJson = json;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            context.RecipeDrafts.Add(new RecipeDraft
            {
                HouseholdId = householdId,
                UserId = userId,
                RecipeId = recipeId,
                DraftJson = json,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await context.SaveChangesAsync(cancellationToken);
        _logger.LogDebug("Saved draft for recipe {RecipeId} by user {UserId}", recipeId, userId);
    }

    public async Task<RecipeDraftData?> GetDraftAsync(int householdId, int userId, int? recipeId, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var draft = await context.RecipeDrafts
            .FirstOrDefaultAsync(d =>
                d.HouseholdId == householdId &&
                d.UserId == userId &&
                d.RecipeId == recipeId,
                cancellationToken);

        if (draft == null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<RecipeDraftData>(draft.DraftJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize draft for recipe {RecipeId}", recipeId);
            return null;
        }
    }

    public async Task DeleteDraftAsync(int householdId, int userId, int? recipeId, CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var draft = await context.RecipeDrafts
            .FirstOrDefaultAsync(d =>
                d.HouseholdId == householdId &&
                d.UserId == userId &&
                d.RecipeId == recipeId,
                cancellationToken);

        if (draft != null)
        {
            context.RecipeDrafts.Remove(draft);
            await context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Deleted draft for recipe {RecipeId} by user {UserId}", recipeId, userId);
        }
    }
}
