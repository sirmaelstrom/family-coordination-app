// ─────────────────────────────────────────────────────────────────────────
// TS mirror of the /api/shopping-lists contract (M9 lockstep). Source of truth:
//   - Endpoints/ShoppingListEndpoints.cs (the C# records)
//   - tests/.../Fixtures/ShoppingList/list.json (byte-locked tripwire)
//
// ⚠ CASING: all keys camelCase. No enums.
// ⚠ DATES: `checkedAt` is a FULL ISO-8601 instant (UTC) or null — render it
//   local via new Date(iso) (NEVER new Date('YYYY-MM-DD')).
// ─────────────────────────────────────────────────────────────────────────

export interface ShoppingListItemDto {
  id: number;
  name: string;
  quantity: number | null;
  unit: string | null;
  category: string;
  isChecked: boolean;
  checkedAt: string | null;
  sortOrder: number;
  addedByName: string | null;
  addedByInitials: string | null;
  addedByPictureUrl: string | null;
  version: number;
}

export interface ShoppingListDto {
  id: number;
  name: string;
  isFavorite: boolean;
  isArchived: boolean;
  /** Gates the Regenerate menu action — only meal-plan-generated lists can rebuild. */
  hasMealPlan: boolean;
  items: ShoppingListItemDto[];
}

export interface ShoppingListSummaryDto {
  id: number;
  name: string;
  isFavorite: boolean;
  itemCount: number;
  uncheckedCount: number;
}

/** Past-lists browse row (GET /archived). `createdAt` is a FULL ISO-8601 instant (UTC). */
export interface ArchivedListSummaryDto {
  id: number;
  name: string;
  isFavorite: boolean;
  itemCount: number;
  uncheckedCount: number;
  createdAt: string;
  hasMealPlan: boolean;
}
