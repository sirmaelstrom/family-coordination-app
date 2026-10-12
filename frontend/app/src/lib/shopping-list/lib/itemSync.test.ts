import { describe, expect, it } from 'vitest';
import { adoptItem, adoptReorderVersions, type ReorderSent } from './itemSync';
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

const sent = (heldVersion: number, sortOrder = 3, category = 'Dairy'): ReorderSent => ({ heldVersion, sortOrder, category });

describe('adoptItem (quest 8cf3c806)', () => {
  it('updates the row a poll put in place of the held object, so the next tap carries the new token', () => {
    const held = item(1, 100);
    const rows = [item(1, 100)]; // a poll replaced the list's objects (same token) while the PATCH ran
    adoptItem(rows, held, item(1, 101, { isChecked: true }), 100);

    expect(rows[0].version).toBe(101);
    expect(rows[0].isChecked).toBe(true);
    expect(held.version).toBe(101);
  });

  it("keeps a polled row that already shows someone's later change", () => {
    const held = item(1, 100);
    const rows = [item(1, 102, { quantity: 7 })]; // another member wrote after this tab's PATCH (101)
    adoptItem(rows, held, item(1, 101, { isChecked: true }), 100);

    expect(rows[0].version).toBe(102);
    expect(rows[0].quantity).toBe(7);
  });
});

describe('adoptReorderVersions (quest 8cf3c806)', () => {
  it('gives a moved row the reorder token, order and category when the reorder read it at the held token', () => {
    const rows = [item(1, 100, { sortOrder: 0, category: 'Pantry' })]; // e.g. a poll reverted the drop on screen
    adoptReorderVersions(rows, new Map([[1, sent(100, 3, 'Dairy')]]), [{ id: 1, versionBefore: 100, version: 101 }]);

    expect(rows[0]).toMatchObject({ version: 101, sortOrder: 3, category: 'Dairy' });
  });

  it("does not hand a fresh token to a row whose server content changed since this tab's read", () => {
    // Another member changed the quantity (100 → 150) before this tab reordered; the reorder read 150.
    const rows = [item(1, 100, { quantity: 1 })];
    adoptReorderVersions(rows, new Map([[1, sent(100)]]), [{ id: 1, versionBefore: 150, version: 151 }]);

    expect(rows[0].version).toBe(100); // its next write 409s and reloads instead of overwriting quantity
  });

  it("leaves a row a poll moved to someone else's version alone", () => {
    const rows = [item(1, 150, { isChecked: true })];
    adoptReorderVersions(rows, new Map([[1, sent(100)]]), [{ id: 1, versionBefore: 100, version: 101 }]);

    expect(rows[0].version).toBe(150);
  });
});
