// ─────────────────────────────────────────────────────────────────────────
// TS mirror of the /api/settings contract (M9 lockstep). Source of truth:
//   - Services/Dtos/SettingsDtos.cs (the C# records — named Settings* there to
//     avoid colliding with the recipes/chores DTOs; here they are island-local)
//   - tests/.../Fixtures/Settings/{categories,members}.json (byte-locked tripwires)
//   - Endpoints/SettingsEndpoints.cs (request DTOs)
//
// ⚠ CASING: all keys camelCase. No enums.
// ⚠ DATES (review X5): `deletedAt` is a FULL ISO-8601 instant (UTC) or null —
//   render it local via new Date(iso) (NEVER new Date('YYYY-MM-DD')).
// ─────────────────────────────────────────────────────────────────────────

/** The per-page context the Razor host hands the island via data- attributes. */
export interface ShellContext {
  householdId: number;
  userId: number;
  userName: string;
  view: 'categories' | 'users';
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
