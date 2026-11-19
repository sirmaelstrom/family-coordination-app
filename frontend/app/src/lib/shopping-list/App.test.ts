import { describe, expect, it, vi } from 'vitest';
import { render } from 'svelte/server';

// Quest 7a784201: the Regenerate confirm says only what Regenerate does.
//
// What the server does (src/FamilyCoordinationApp/Services/ShoppingListGenerator.cs, RegenerateOnceAsync):
//   • manually added items are never removed;
//   • every generated item is deleted and rebuilt from the whole plan week;
//   • a rebuilt item takes its checked state and quantity adjustment from the old generated item with the
//     same normalized name AND the same category. Rename or recategorize a generated item and that match
//     is lost: its row comes back unchecked and unadjusted;
//   • name, unit and category always come from the rebuilt row, and editing a generated item's unit
//     clears its quantity adjustment at once (Endpoints/ShoppingListEndpoints.cs, PatchItem).
// The old copy promised "checked-off state and quantity edits are kept" with no condition.
//
// The component is rendered on the server (`svelte/server`), which needs no DOM package: ConfirmDialog
// always renders its <dialog> markup and only toggles it open in an effect, so the copy is in the HTML.
// No effect runs in a server render, so nothing here loads a list; fetch is stubbed to throw anyway.

vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));
vi.mock('$app/navigation', () => ({ goto: vi.fn() }));
// A fetch from this test is a bug: it throws, and the last test asserts it was never called.
const fetchSpy = vi.fn(() => {
  throw new Error('App.test.ts must not reach a network');
});
vi.stubGlobal('fetch', fetchSpy);

import App from './App.svelte';
import type { ShellContext } from '#lib/session.svelte.ts';

const ctx: ShellContext = {
  householdId: 1,
  userId: 1,
  userName: 'Test Member',
  initials: 'TM',
  pictureUrl: null,
  isSiteAdmin: false,
  listId: null,
  recipeId: null,
};

/** Text of every confirm/prompt dialog in the rendered page, keyed by its <h2> title. */
function dialogMessages(html: string): Map<string, string> {
  const messages = new Map<string, string>();
  // Server output carries hydration comments (<!---->) between nodes; drop them before matching.
  const clean = html.replace(/<!--.*?-->/gs, '');
  for (const m of clean.matchAll(/<h2[^>]*>([^<]*)<\/h2>\s*<p[^>]*>([^<]*)<\/p>/g)) {
    messages.set(decode(m[1]), decode(m[2]));
  }
  return messages;
}

function decode(text: string): string {
  return text
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&amp;/g, '&')
    .trim();
}

describe('shopping list: the Regenerate confirm', () => {
  const message = dialogMessages(render(App, { props: { ctx } }).body).get('Regenerate List');

  it('is rendered by the shopping list page', () => {
    expect(message).toBeDefined();
  });

  it('says exactly what Regenerate keeps', () => {
    expect(message).toBe(
      'Rebuild this list from the whole week of its meal plan? Items you added yourself are kept. ' +
        'Items from the meal plan keep their check marks and quantity changes as long as their name, ' +
        'unit and category are unchanged.',
    );
  });

  it('does not promise check marks or quantity changes without the condition', () => {
    // Whatever the wording becomes, the sentence that makes the promise must carry its condition:
    // name and category are the carry-over match key, and a unit edit clears the quantity adjustment
    // (src/FamilyCoordinationApp/Endpoints/ShoppingListEndpoints.cs, PatchItem).
    const promise = (message ?? '').split(/(?<=[.?;])\s+/).filter((s) => /check|quantit/i.test(s));
    expect(promise.length).toBeGreaterThan(0);
    for (const sentence of promise) {
      expect(sentence).toMatch(/\bname\b/i);
      expect(sentence).toMatch(/\bunit\b/i);
      expect(sentence).toMatch(/\bcategory\b/i);
    }
  });

  it('renders without a network call', () => {
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});
