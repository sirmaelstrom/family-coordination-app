import { describe, expect, it } from 'vitest';
import { adoptItem, adoptReorderVersions } from './itemSync';
import type { ShoppingListItemDto } from './types';

const item = (id: number, version: number, over: Partial<ShoppingListItemDto> = {}): ShoppingListItemDto => ({
  id,
  name: `item ${id}`,
  quantity: 1,
  unit: null,
  category: 'Pantry',
  isChecked: false,
  checkedAt: null,
  sortOrder: 0,
  addedByName: null,
  addedByInitials: null,
  addedByPictureUrl: null,
  version,
  ...over,
});

describe('adoptItem (quest 8cf3c806)', () => {
  it('updates the row a poll put in place of the held object, so the next tap carries the new token', () => {
    const held = item(1, 100);
    const rows = [item(1, 100)]; // a poll replaced the list's objects while the PATCH ran
    const answer = item(1, 101, { isChecked: true });

    adoptItem(rows, held, answer);

    expect(rows[0].version).toBe(101);
    expect(rows[0].isChecked).toBe(true);
    expect(held.version).toBe(101);
  });

  it('is a no-op on the rows when the held object is the row itself', () => {
    const held = item(1, 100);
    const rows = [held];
    adoptItem(rows, held, item(1, 101));
    expect(rows[0]).toBe(held);
    expect(held.version).toBe(101);
  });
});

describe('adoptReorderVersions (quest 8cf3c806)', () => {
  it('gives a moved row the reorder token when it is still at the token this tab held', () => {
    const rows = [item(1, 100), item(2, 200)];
    adoptReorderVersions(rows, new Map([[1, 100], [2, 200]]), [{ id: 1, version: 101 }, { id: 2, version: 200 }]);
    expect(rows.map((r) => r.version)).toEqual([101, 200]);
  });

  it("leaves a row a poll moved to someone else's version alone, so it 409s instead of overwriting unseen", () => {
    const rows = [item(1, 150, { isChecked: true })]; // another member's check, seen by a poll mid-reorder
    adoptReorderVersions(rows, new Map([[1, 100]]), [{ id: 1, version: 151 }]);
    expect(rows[0].version).toBe(150);
  });
});
