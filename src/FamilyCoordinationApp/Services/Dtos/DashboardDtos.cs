using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Dtos;

// ─────────────────────────────────────────────────────────────────────────
// Dashboard island aggregate DTOs (strangler — mirrors the meal-plan/chores
// M9 lockstep). Source of truth for the island TS contract:
// tests/FamilyCoordinationApp.Tests/Fixtures/Dashboard/dashboard.json +
// frontend/dashboard/src/lib/types.ts. A shape/casing change updates THIS file,
// that fixture, and types.ts in lockstep (DashboardDtoContractTests is the tripwire).
//
// ⚠ CASING: MealType is a real enum TYPE on the DTO → it serializes as a camelCase
//   string ("breakfast"/"lunch"/"dinner"/"snack") via the globally-registered
//   JsonStringEnumConverter(CamelCase) (Program.cs ConfigureHttpJsonOptions).
//   DateOnly serializes as "YYYY-MM-DD".
//
// Read-only island: the dashboard mutates NOTHING — no write DTOs, no concurrency
// token. The card counts are pure aggregation over the existing services.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// The whole dashboard read-aggregate in one payload (one round-trip, D1). <see cref="Today"/> is the
/// SERVER's "today" (used for the meals query AND the display labels) echoed so the card header can't drift
/// from the data — the island formats it client-side (noon-UTC, never <c>new Date("YYYY-MM-DD")</c>).
/// </summary>
public sealed record DashboardDto(
    string GreetingName,
    string HouseholdName,
    DateOnly Today,
    DashboardChoreSummaryDto Chores,
    DashboardShoppingSummaryDto Shopping,
    IReadOnlyList<DashboardMealDto> TodaysMeals);

/// <summary>
/// The four Home chore-card counts — mirrors <see cref="ChoreHomeStats.Result"/> field-for-field. The
/// "needs attention" figure (<c>Overdue + DueToday</c>) is DISPLAY logic the island derives; it is not a
/// wire field (the DTO stays the raw reducer output).
/// </summary>
public sealed record DashboardChoreSummaryDto(
    int ActiveTotal,
    int Overdue,
    int DueToday,
    int UpForGrabs);

/// <summary>
/// Shopping summary across ALL active (non-archived) lists. The progress percentage
/// (<c>Total &gt; 0 ? Checked * 100 / Total : 0</c>) is DISPLAY logic the island derives; <see cref="Total"/>
/// is echoed for the "{Checked} of {Total} items checked" label.
/// </summary>
public sealed record DashboardShoppingSummaryDto(
    int Remaining,
    int Checked,
    int Total);

/// <summary>One of today's planned meals — its meal type and the resolved display name (recipe name, else
/// the custom-meal name, else "Unnamed meal"). The list is pre-ordered by <see cref="MealType"/>.</summary>
public sealed record DashboardMealDto(
    MealType MealType,
    string DisplayName);
