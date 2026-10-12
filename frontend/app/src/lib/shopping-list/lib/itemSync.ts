import type { ShoppingListItemDto } from './types';

// How a write's answer reaches the rows the list shows (quest 8cf3c806). Every item PATCH carries the item's token
// and a stale one is a 409, so two rules hold here:
//   1. a row keeps no OLD token after this tab's own write (or the next tap 409s against that write), and
//   2. a row is never given a NEWER token than its content (or the next tap overwrites a change it never showed).

/**
 * Adopt a PATCH answer on the object the caller held AND on the row the list shows now. A poll that finished while
 * the write was in flight may have replaced the list's objects, so the held one may be detached. The shown row takes
 * the answer only while it is still at the token the PATCH sent (`sentVersion`): a row a poll moved past that shows
 * someone's later change, and it keeps it (rule 2; its newer token is the right one to send next).
 */
export function adoptItem(
  rows: ShoppingListItemDto[] | undefined,
  held: ShoppingListItemDto,
  updated: ShoppingListItemDto,
  sentVersion: number,
): void {
  Object.assign(held, updated);
  const row = rows?.find((i) => i.id === updated.id);
  if (row && row !== held && row.version === sentVersion) Object.assign(row, updated);
}

/** What this tab sent for one moved item, and the token it held for it. */
export interface ReorderSent {
  heldVersion: number;
  sortOrder: number;
  category: string;
}

/**
 * Adopt a reorder's answer on the rows shown now. A row takes the post-save token only when the reorder READ it at
 * the token this tab held (`versionBefore === heldVersion`), so its content is what the server had; it also takes
 * the order and category this tab sent, which a poll that read before the commit may have reverted. Any other row
 * keeps its token, so its next write 409s and reloads instead of overwriting a change it never showed (rule 2).
 */
export function adoptReorderVersions(
  rows: ShoppingListItemDto[] | undefined,
  sent: ReadonlyMap<number, ReorderSent>,
  answered: readonly { id: number; versionBefore: number; version: number }[],
): void {
  for (const { id, versionBefore, version } of answered) {
    const mine = sent.get(id);
    const row = rows?.find((i) => i.id === id);
    if (!mine || !row || versionBefore !== mine.heldVersion || row.version !== mine.heldVersion) continue;
    row.version = version;
    row.sortOrder = mine.sortOrder;
    row.category = mine.category;
  }
}
