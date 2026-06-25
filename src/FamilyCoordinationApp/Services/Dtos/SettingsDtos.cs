namespace FamilyCoordinationApp.Services.Dtos;

// ─────────────────────────────────────────────────────────────────────────
// Settings island A (Household settings) aggregate DTOs — Categories + Manage
// Users (strangler; mirrors the dashboard/recipes M9 lockstep). Source of truth
// for the island TS contract:
//   tests/.../Fixtures/Settings/{categories,members}.json + frontend/settings/src/lib/types.ts.
// A shape/casing change updates THIS file, those fixtures, and types.ts in
// lockstep (CategoryListDtoContractTests / MemberListDtoContractTests are the tripwires).
//
// ⚠ CASING: all keys camelCase (JsonSerializerDefaults.Web).
// ⚠ DATES (review X5): Category.DeletedAt is a FULL INSTANT (DateTime?, UTC) —
//   it serializes as a round-trip ISO-8601 string ("2026-06-20T14:30:00Z"), NOT
//   a bare "YYYY-MM-DD". The island renders it local via new Date(iso) (never
//   new Date('YYYY-MM-DD')). No enums here ⇒ plain camelCase.
// ─────────────────────────────────────────────────────────────────────────

// NOTE: named Settings* to avoid colliding with the recipes-island CategoryDto (RecipeDtos.cs) and the
// chores MemberDto (ChoreDtos.cs) — each island keeps its own DTO shape in this shared namespace.

/// <summary>Categories view payload: the active list (sorted) + the deleted list (parity Categories.razor:162-167).</summary>
public sealed record CategoryListDto(
    IReadOnlyList<SettingsCategoryDto> Active,
    IReadOnlyList<SettingsCategoryDto> Deleted);

/// <summary>One household category. <see cref="DeletedAt"/> is null for active rows; a UTC instant for deleted ones.</summary>
public sealed record SettingsCategoryDto(
    int CategoryId,
    string Name,
    string? IconEmoji,
    string Color,
    bool IsDefault,
    int SortOrder,
    DateTime? DeletedAt);

/// <summary>Manage-Users payload: the caller's id (so the island renders "You" + gates self-actions without a second call) + the members.</summary>
public sealed record MemberListDto(
    int CurrentUserId,
    IReadOnlyList<SettingsMemberDto> Members);

/// <summary>One household member (email-ordered).</summary>
public sealed record SettingsMemberDto(
    int UserId,
    string Email,
    string? DisplayName,
    bool IsWhitelisted);

/// <summary>
/// The result of an add-member call. <see cref="Outcome"/> is "created" | "reenabled" | "alreadyActive"
/// (the cross-household collision is a 409, NOT an outcome here — review R-A1). The island toasts "created"/
/// "reenabled" as success and "alreadyActive" as a WARNING (parity: today "already has access" is a warning).
/// </summary>
public sealed record MemberActionDto(
    SettingsMemberDto Member,
    string Outcome);
