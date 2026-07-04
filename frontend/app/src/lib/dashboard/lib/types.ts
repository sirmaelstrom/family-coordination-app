// ─────────────────────────────────────────────────────────────────────────
// Dashboard island TS contract. MIRRORS the server DTOs in
// src/FamilyCoordinationApp/Services/Dtos/DashboardDtos.cs — kept in lockstep
// with tests/.../Fixtures/Dashboard/dashboard.json (DashboardDtoContractTests
// is the tripwire). A field rename / casing change here means the same change
// in that DTO + fixture (M9).
//
// ⚠ CASING: MealType is a real enum on the server DTO → it arrives as a
//   camelCase string ("breakfast"/"lunch"/"dinner"/"snack"). `today` is a
//   DateOnly → "YYYY-MM-DD"; format it for DISPLAY at noon-UTC, never
//   `new Date("YYYY-MM-DD")` (UTC-midnight parse = wrong day in US tz).
// ─────────────────────────────────────────────────────────────────────────

export type MealType = 'breakfast' | 'lunch' | 'dinner' | 'snack';

/** The four Home chore-card counts (mirrors ChoreHomeStats.Result). */
export interface DashboardChoreSummaryDto {
  activeTotal: number;
  overdue: number;
  dueToday: number;
  upForGrabs: number;
}

/** Shopping totals summed across all active (non-archived) lists. */
export interface DashboardShoppingSummaryDto {
  remaining: number;
  checked: number;
  total: number;
}

/** One of today's planned meals — type + resolved display name. */
export interface DashboardMealDto {
  mealType: MealType;
  displayName: string;
}

/** The whole dashboard read-aggregate (one round-trip). */
export interface DashboardDto {
  greetingName: string;
  householdName: string;
  today: string; // "YYYY-MM-DD"
  chores: DashboardChoreSummaryDto;
  shopping: DashboardShoppingSummaryDto;
  todaysMeals: DashboardMealDto[];
}

/** Shell context the Razor host hands the island via root data-attrs. */
export interface ShellContext {
  householdId: number;
  userId: number;
  userName: string;
}
