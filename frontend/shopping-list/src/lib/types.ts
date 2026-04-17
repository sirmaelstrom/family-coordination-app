export interface ShellContext {
  householdId: number;
  listId: number | null;
  userId: number;
  userName: string;
}

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
  items: ShoppingListItemDto[];
}

export interface ShoppingListSummaryDto {
  id: number;
  name: string;
  isFavorite: boolean;
  itemCount: number;
  uncheckedCount: number;
}
