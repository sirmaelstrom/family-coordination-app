import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from 'svelte/server';

// What the meal plan draws for a URL, a store week and a board (quest 70f237a1, from fca#170's review).
// Three things name a week: the URL (what is on screen), the store (which follows the URL one effect
// later) and the board (which stays the old week's until the new one arrives or fails). Slots may be
// drawn only when all three agree; otherwise a populated day reads as four empty, clickable slots.
//
// These are SERVER renders of the real App template (vitest's `node` environment compiles .svelte for
// the server), so no effect runs: each test sets the store to the state it is about, and the output
// is what the template draws for that state.

const nav = vi.hoisted(() => ({ url: new URL('http://fca.test/meal-plan') }));
vi.mock('$app/state', () => ({
  page: {
    get url() {
      return nav.url;
    },
  },
}));
vi.mock('$app/navigation', () => ({ goto: vi.fn() }));
vi.mock('$app/paths', () => ({ resolve: (path: string) => path }));

import App from './App.svelte';
import { mealPlanStore as store } from './lib/state.svelte';
import type { MealPlanBoardDto, MealPlanEntryDto, ShellContext } from './lib/types';

const W1 = '2026-11-23'; // Mon Nov 23 – Sun Nov 29
const W2 = '2026-11-30'; // Mon Nov 30 – Sun Dec 6
const SUNDAY = '2026-11-29';
const MONDAY = '2026-11-30';

const CTX: ShellContext = {
  householdId: 1,
  userId: 1,
  userName: 'Test Member',
  initials: 'TM',
  pictureUrl: null,
  isSiteAdmin: false,
  listId: null,
  recipeId: null,
};

function dinner(entryId: number, date: string, name: string): MealPlanEntryDto {
  return {
    mealPlanId: 1,
    entryId,
    date,
    mealType: 'dinner',
    recipe: null,
    customMealName: name,
    notes: null,
    servings: null,
    version: 100 + entryId,
  };
}
const boardW1 = (): MealPlanBoardDto => ({
  weekStartDate: W1,
  mealPlanId: 1,
  entries: [dinner(1, SUNDAY, 'Sunday Roast')],
});

/** The store as it stands, drawn for this URL. */
function draw(search: string): string {
  nav.url = new URL(`http://fca.test/meal-plan${search}`);
  store.rebuildZones(); // App's $effect.pre in the browser; effects do not run in a server render
  return render(App, { props: { ctx: CTX } }).body;
}
const emptySlots = (html: string): number => (html.match(/aria-label="Add a meal"/g) ?? []).length;
const dayHeaders = (html: string): number => (html.match(/in day view/g) ?? []).length;
const LOADING = 'Loading the meal plan…';

describe('meal plan: slots come only from the board for the week on screen', () => {
  let desktop = false;

  beforeEach(() => {
    desktop = false;
    vi.stubGlobal('window', { matchMedia: () => ({ matches: desktop }) });
    store.weekStart = W1;
    store.board = boardW1();
    store.loading = false;
    store.error = null;
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('draws a day from the board for its week', () => {
    const html = draw(`?view=day&date=${SUNDAY}`);
    expect(html).toContain('Sunday Roast');
    expect(emptySlots(html)).toBe(3); // breakfast, lunch, snack
    expect(html).not.toContain(LOADING);
  });

  it('does not draw a day in another week from the board in hand, even before the store has followed the URL', () => {
    // The URL moved to Monday of the next week; the store and its board are still on last week.
    const html = draw(`?view=day&date=${MONDAY}`);
    expect(html).toContain('Monday, November 30, 2026');
    expect(emptySlots(html)).toBe(0);
    expect(html).toContain(LOADING);
  });

  it('does not draw another week under the new week label (desktop grid, phone list)', () => {
    for (const isDesktop of [true, false]) {
      desktop = isDesktop;
      const html = draw(`?view=week&date=${MONDAY}`);
      expect(html).toContain('Nov 30 – Dec 6, 2026');
      expect(emptySlots(html)).toBe(0);
      expect(dayHeaders(html)).toBe(0);
      expect(html).not.toContain('Sunday Roast');
      expect(html).toContain(LOADING);
    }
  });

  it('shows the loading text while the other week is being read', () => {
    store.weekStart = W2; // changeWeek has run; the old board is still in hand
    const html = draw(`?view=day&date=${MONDAY}`);
    expect(emptySlots(html)).toBe(0);
    expect(html).toContain(LOADING);
  });

  it('shows the error with Retry, and no slots, when the other week failed to load', () => {
    store.weekStart = W2;
    store.error = 'Failed to load the meal plan (HTTP 500).';
    const html = draw(`?view=day&date=${MONDAY}`);
    expect(html).toContain('Failed to load the meal plan (HTTP 500).');
    expect(html).toMatch(/<button[^>]*class="mp-retry[^"]*"[^>]*>Retry<\/button>/);
    expect(emptySlots(html)).toBe(0);
    expect(html).not.toContain(LOADING);
  });

  it('does not draw an empty day when the URL steps back to the week in hand before the store follows', () => {
    // Sunday → Monday → back to Sunday while Monday's week is still loading: the board is Sunday's
    // week again, but the store (and so its slot zones) still names Monday's.
    store.weekStart = W2;
    const html = draw(`?view=day&date=${SUNDAY}`);
    expect(emptySlots(html)).toBe(0);
    expect(html).toContain(LOADING);
  });
});
