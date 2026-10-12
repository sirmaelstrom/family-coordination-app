import type { ShoppingListItemDto } from './types';

// How a write's answer reaches the rows the list shows (quest 8cf3c806). Every item PATCH carries the item's token
// and a stale one is a 409, so a row that keeps an old token after this tab's own write makes the next tap fail.

/**
 * Adopt a PATCH answer on the object the caller held AND on the row the list shows now. A poll that finished while
 * the write was in flight replaced the list's objects, so the held one may be detached.
 */
export function adoptItem(
  rows: ShoppingListItemDto[] | undefined,
  held: ShoppingListItemDto,
  updated: ShoppingListItemDto,
): void {
  Object.assign(held, updated);
  const row = rows?.find((i) => i.id === updated.id);
  if (row && row !== held) Object.assign(row, updated);
}

/**
 * Adopt a reorder's answered tokens on the rows shown now, but only on a row still at the token this tab held
 * before the reorder. A row a poll moved to another version shows someone else's change, and giving it the
 * reorder's token would let the next tap overwrite that change unseen; such a row keeps its token and 409s instead.
 */
export function adoptReorderVersions(
  rows: ShoppingListItemDto[] | undefined,
  versionsBefore: ReadonlyMap<number, number>,
  answered: readonly { id: number; version: number }[],
): void {
  for (const { id, version } of answered) {
    const row = rows?.find((i) => i.id === id);
    if (row && row.version === versionsBefore.get(id)) row.version = version;
  }
}
