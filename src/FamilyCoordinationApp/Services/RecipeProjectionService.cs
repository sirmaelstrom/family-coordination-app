using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// The single recipe→DTO projection for the recipes island (mirrors <see cref="MealPlanBoardService"/>'s
/// projection pattern). Stateless — no DB access; callers pass already-loaded entities. <see cref="ToFull"/>
/// server-sanitizes the markdown instructions via <see cref="MarkdownHelper.ToSafeHtml"/> so the island ships
/// no Markdown library (render via {@html}); it ALSO returns the raw markdown for the edit textarea.
/// </summary>
public class RecipeProjectionService : IRecipeProjectionService
{
    public RecipeListItemDto ToListItem(Recipe recipe) => new(
        recipe.RecipeId,
        recipe.Name,
        recipe.RecipeType,
        recipe.ImagePath,
        !string.IsNullOrWhiteSpace(recipe.SourceUrl),
        recipe.CreatedBy?.DisplayName,
        recipe.CreatedBy?.PictureUrl,
        recipe.Ingredients.OrderBy(i => i.SortOrder).Take(3).Select(i => i.Name).ToList(),
        recipe.Ingredients.Count);

    public RecipeFullDto ToFull(Recipe recipe, bool includeAuthor = true) => new(
        recipe.RecipeId,
        recipe.Version,
        recipe.Name,
        recipe.RecipeType,
        recipe.Description,
        recipe.Instructions,
        MarkdownHelper.ToSafeHtml(recipe.Instructions),
        recipe.ImagePath,
        recipe.SourceUrl,
        recipe.PrepTimeMinutes,
        recipe.CookTimeMinutes,
        recipe.Servings,
        includeAuthor ? recipe.CreatedBy?.DisplayName : null,
        includeAuthor ? recipe.CreatedBy?.PictureUrl : null,
        recipe.SharedFromHouseholdName,
        recipe.Ingredients
            .OrderBy(i => i.SortOrder)
            .Select(i => new RecipeIngredientFullDto(
                i.IngredientId, i.Quantity, i.Unit, i.Name, i.Category, i.Notes, i.GroupName, i.SortOrder))
            .ToList());

    public ParsedIngredientDto ToParsed(ParsedIngredient parsed, string suggestedCategory) => new(
        parsed.Quantity,
        parsed.Unit,
        parsed.Name,
        parsed.Notes,
        parsed.IsComplete,
        suggestedCategory);
}
