using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Dtos;

// ─────────────────────────────────────────────────────────────────────────
// Recipes island DTOs (strangler — mirrors the meal-plan M9 lockstep).
// Source of truth for the island TS contract: tests/FamilyCoordinationApp.Tests/
// Fixtures/RecipeList/list.json + Fixtures/RecipeFull/recipe.json +
// frontend/recipes/src/lib/types.ts. A shape/casing change updates THIS file,
// those fixtures, and types.ts in lockstep (RecipeListDtoContractTests +
// RecipeFullDtoContractTests are the tripwires).
//
// ⚠ CASING: RecipeType is a real enum TYPE on the DTO → it serializes as a
//   camelCase string via the globally-registered JsonStringEnumConverter(CamelCase)
//   (Program.cs ConfigureHttpJsonOptions). decimal?/int? serialize as number-or-null.
//
// Kept SEPARATE from MealPlanDtos.cs (D11): the meal-plan RecipeDetailDto is a lean
// board-card view; the recipes RecipeFullDto is a read-drawer + edit-form superset
// (raw markdown + sanitized HTML + per-ingredient Category/GroupName/IngredientId).
//
// Drafts reuse DraftService's existing RecipeDraftData / IngredientDraftData records
// directly (no draft DTO here) — they already serialize camelCase via the web defaults.
//
// Optimistic concurrency: RecipeFullDto carries the xmin token (Version) so the
// edit form can send it back on PUT; a stale token → 409 (RecipeConflictException).
// ─────────────────────────────────────────────────────────────────────────

// ── List ──────────────────────────────────────────────────────────────────

/// <summary>The recipe grid payload: the cards + this user's favorite ids (empty for connected households).</summary>
public sealed record RecipeListDto(
    IReadOnlyList<RecipeListItemDto> Recipes,
    IReadOnlyList<int> FavoriteRecipeIds);

/// <summary>One card. <see cref="HasSourceUrl"/> is a bool (the card shows only the "imported" icon, not the url).</summary>
public sealed record RecipeListItemDto(
    int RecipeId,
    string Name,
    RecipeType RecipeType,
    string? ImagePath,
    bool HasSourceUrl,
    string? CreatedByName,            // null when connected (privacy) or deleted user
    string? CreatedByPictureUrl,      // User has PictureUrl + Initials, NO color field → card renders pic-or-initials
    IReadOnlyList<string> IngredientPreview,   // first 3 ingredient names (ordered by sort order)
    int IngredientCount);             // for the "+N more" suffix

// ── Full (read drawer + edit form — superset, D3) ──────────────────────────

/// <summary>
/// Read-drawer + edit-form detail. <see cref="InstructionsHtml"/> is server-sanitized
/// (<see cref="MarkdownHelper.ToSafeHtml"/>) for the drawer's {@html}; <see cref="Instructions"/> is the raw
/// markdown for the edit textarea. Author fields are null when projected with includeAuthor:false (connected).
/// </summary>
public sealed record RecipeFullDto(
    int RecipeId,
    uint Version,                     // xmin concurrency token — echoed back on PUT (stale ⇒ 409)
    string Name,
    RecipeType RecipeType,
    string? Description,
    string? Instructions,             // RAW markdown — for the edit textarea
    string InstructionsHtml,          // server-sanitized — for the read drawer ({@html})
    string? ImagePath,
    string? SourceUrl,
    int? PrepTimeMinutes,
    int? CookTimeMinutes,
    int? Servings,
    string? CreatedByName,            // both null when connected (includeAuthor:false) / deleted user
    string? CreatedByPictureUrl,
    string? SharedFromHouseholdName,  // attribution for copied recipes
    IReadOnlyList<RecipeIngredientFullDto> Ingredients);

/// <summary>One ingredient line for the edit form / detail (full shape incl. category/group/id).</summary>
public sealed record RecipeIngredientFullDto(
    int IngredientId,
    decimal? Quantity,
    string? Unit,
    string Name,
    string Category,
    string? Notes,
    string? GroupName,
    int SortOrder);

// ── Parse / categories / connections / import ──────────────────────────────

/// <summary>A natural-language-parsed ingredient + the inferred category (the entry form's parse-on-blur result).</summary>
public sealed record ParsedIngredientDto(
    decimal? Quantity,
    string? Unit,
    string Name,
    string? Notes,
    bool IsComplete,
    string SuggestedCategory);

/// <summary>A household category name (for the ingredient-entry category select).</summary>
public sealed record CategoryDto(string Name);

/// <summary>A connected household for the selector. Intentionally drops ConnectedHouseholdInfo.ConnectedAt.</summary>
public sealed record ConnectedHouseholdDto(int HouseholdId, string HouseholdName);

/// <summary>
/// Result of a URL import. On success, <see cref="RecipeId"/> is the SAVED recipe's id (the endpoint persists
/// the unsaved entity via CreateRecipeAsync). On a duplicate, <see cref="ExistingRecipeId"/> / Name are set. On
/// failure, <see cref="ErrorType"/> (the RecipeImportErrorType name) + optional <see cref="PartialData"/>.
/// </summary>
public sealed record RecipeImportResultDto(
    bool Success,
    int? RecipeId,
    string? ErrorMessage,
    string? ErrorType,
    int? ExistingRecipeId,
    string? ExistingRecipeName,
    PartialRecipeDataDto? PartialData);

/// <summary>Partially-extracted import data for manual completion when full extraction fails.</summary>
public sealed record PartialRecipeDataDto(
    string? Name,
    string? Description,
    string? Instructions,
    IReadOnlyList<string>? IngredientStrings,
    string? ImageUrl,
    int? PrepTimeMinutes,
    int? CookTimeMinutes,
    int? Servings);
