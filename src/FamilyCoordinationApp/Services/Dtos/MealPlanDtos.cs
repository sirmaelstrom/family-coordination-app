using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Dtos;

// ─────────────────────────────────────────────────────────────────────────
// Meal-plan island board DTOs (strangler — mirrors the chores M9 lockstep).
// Source of truth for the island TS contract: tests/FamilyCoordinationApp.Tests/
// Fixtures/MealPlanBoard/board.json + frontend/meal-plan/src/lib/types.ts. A
// shape/casing change updates THIS file, that fixture, and types.ts in lockstep
// (MealPlanBoardDtoContractTests is the tripwire).
//
// ⚠ CASING: both enums below (MealType, RecipeType) are real enum TYPES on the
//   DTO → they serialize as camelCase strings via the globally-registered
//   JsonStringEnumConverter(CamelCase) (Program.cs ConfigureHttpJsonOptions).
//   This deliberately avoids the chores recurrenceMode/effortTier PascalCase
//   plain-string trap. DateOnly serializes as "YYYY-MM-DD".
//
// Parity-first (no drag-drop) ⇒ versionless / last-write-wins: NO xmin token on
// the wire. MealPlanEntry.Version exists on the entity but is unused here.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>The week board: the Monday it covers, the plan id (null when none yet), and its entries.</summary>
public sealed record MealPlanBoardDto(
    DateOnly WeekStartDate,
    int? MealPlanId,
    IReadOnlyList<MealPlanEntryDto> Entries);

/// <summary>
/// One planned meal. Exactly one of <see cref="Recipe"/> / <see cref="CustomMealName"/> is set.
/// <see cref="Servings"/> is how many this meal is being cooked for; null means "as the recipe is written",
/// which is what shopping-list generation scales against.
/// </summary>
public sealed record MealPlanEntryDto(
    int MealPlanId,
    int EntryId,
    DateOnly Date,
    MealType MealType,
    MealRecipeSummaryDto? Recipe,
    string? CustomMealName,
    string? Notes,
    int? Servings,
    /// <summary>
    /// The row's xmin concurrency token. Send it back on every mutation of this entry (move / servings /
    /// remove); a stale token is a 409 rather than a silent overwrite.
    /// </summary>
    uint Version);

/// <summary>
/// The lightweight recipe shape a slot card renders (board payload stays lean). <see cref="Servings"/> is the
/// recipe's own yield — the denominator the entry's override is relative to, so the client can show what a
/// change actually means rather than a bare number.
/// </summary>
public sealed record MealRecipeSummaryDto(
    int RecipeId,
    string Name,
    string? ImagePath,
    RecipeType RecipeType,
    int? Servings);

/// <summary>
/// Read-only recipe detail for the in-island view modal. <see cref="InstructionsHtml"/> is server-sanitized
/// (<see cref="MarkdownHelper.ToSafeHtml"/>) so the island ships no Markdown library — render via {@html}.
/// </summary>
public sealed record RecipeDetailDto(
    int RecipeId,
    string Name,
    string? ImagePath,
    RecipeType RecipeType,
    int? PrepTimeMinutes,
    int? CookTimeMinutes,
    int? Servings,
    string InstructionsHtml,
    IReadOnlyList<RecipeIngredientDto> Ingredients);

/// <summary>One ingredient line, pre-ordered by sort order.</summary>
public sealed record RecipeIngredientDto(
    decimal? Quantity,
    string? Unit,
    string Name,
    string? Notes,
    int SortOrder);
