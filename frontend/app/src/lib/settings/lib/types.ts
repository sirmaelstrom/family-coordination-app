// ─────────────────────────────────────────────────────────────────────────
// TS mirror of the /api/settings contract (M9 lockstep). Source of truth:
//   - Services/Dtos/SettingsDtos.cs (the C# records — named Settings* there to
//     avoid colliding with the recipes/chores DTOs; here they are island-local)
//   - tests/.../Fixtures/Settings/{categories,members}.json (byte-locked tripwires)
//   - Endpoints/SettingsEndpoints.cs (request DTOs)
// The Deleted Recipes view mirrors two /api/recipes routes instead (Services/Dtos/RecipeDtos.cs,
// Fixtures/RecipeList/deleted.json).
//
// ⚠ CASING: all keys camelCase. No enums.
// ⚠ DATES (review X5): `deletedAt` is a FULL ISO-8601 instant (UTC) or null —
//   render it local via new Date(iso) (NEVER new Date('YYYY-MM-DD')).
// ─────────────────────────────────────────────────────────────────────────

/** The per-page context the Razor host hands the island via data- attributes. */
import type { ShellContext as AppShellContext } from '#lib/session.svelte.ts';

// Canonical identity + this island's view discriminator (quest 76e6f169).
export interface ShellContext extends AppShellContext {
  view: 'categories' | 'users' | 'deleted-recipes';
}

// ── Deleted recipes (Fixtures/RecipeList/deleted.json; GET /api/recipes/deleted) ──

import type { RecipeType } from '#lib/api/recipeType.ts';

export interface DeletedRecipeDto {
  recipeId: number;
  name: string;
  recipeType: RecipeType;
  /** full ISO-8601 instant (UTC) of the delete, or null when the delete was never stamped. */
  deletedAt: string | null;
}

export interface DeletedRecipeListDto {
  /** Most recently deleted first. */
  recipes: DeletedRecipeDto[];
}

// ── Categories (Fixtures/Settings/categories.json) ──────────────────────────

export interface CategoryDto {
  categoryId: number;
  name: string;
  iconEmoji: string | null;
  color: string;
  isDefault: boolean;
  sortOrder: number;
  /** full ISO-8601 instant (UTC) for deleted rows, else null. */
  deletedAt: string | null;
}

export interface CategoryListDto {
  active: CategoryDto[];
  deleted: CategoryDto[];
}

export interface CategoryWriteRequest {
  name: string;
  iconEmoji: string | null;
  color: string;
}

// ── Members (Fixtures/Settings/members.json) ────────────────────────────────

export interface MemberDto {
  userId: number;
  email: string;
  displayName: string | null;
  isWhitelisted: boolean;
}

export interface MemberListDto {
  currentUserId: number;
  members: MemberDto[];
}

/** Add-member outcome envelope (review R-A1): "alreadyActive" is a WARNING, not an error. */
export type AddMemberOutcome = 'created' | 'reenabled' | 'alreadyActive';

export interface MemberActionDto {
  member: MemberDto;
  outcome: AddMemberOutcome;
}
