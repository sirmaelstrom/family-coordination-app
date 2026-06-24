using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services.Interfaces;

/// <summary>
/// The ONE projection from <see cref="Recipe"/> (+ a parsed ingredient) to the recipes-island DTOs — so the
/// list cards, the read drawer, and the edit form can't drift (M9, mirrors <see cref="IMealPlanBoardService"/>'s
/// single-projection rule). Pure mapping; callers load the entity (with the navs the projection reads) and
/// filter by HouseholdId (M1).
/// </summary>
public interface IRecipeProjectionService
{
    /// <summary>A grid card. Reads <c>Ingredients</c> (for the preview/count) and <c>CreatedBy</c> (author).</summary>
    RecipeListItemDto ToListItem(Recipe recipe);

    /// <summary>
    /// The read-drawer + edit-form superset. Reads <c>Ingredients</c> + <c>CreatedBy</c>.
    /// <paramref name="includeAuthor"/> = false strips the author fields (connected-household reads — privacy).
    /// </summary>
    RecipeFullDto ToFull(Recipe recipe, bool includeAuthor = true);

    /// <summary>A natural-language-parsed ingredient + the inferred category (the entry form's parse result).</summary>
    ParsedIngredientDto ToParsed(ParsedIngredient parsed, string suggestedCategory);
}
