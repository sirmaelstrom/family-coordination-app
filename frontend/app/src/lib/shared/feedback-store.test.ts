// Feedback rides the one HTTP boundary (PR #110 council round-1 amendment).
// NOTE on ordering: the store is a module singleton and the 401 case leaves a
// never-settling submit (submitting stays latched while the page navigates),
// so the ordinary-rejection case runs FIRST.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./toast-store.svelte', () => ({ showToast: vi.fn() }));

import { feedbackError, feedbackOpen, openFeedback, submitFeedback } from './feedback-store.svelte';
import { LOGIN_URL, setNavigateForTesting } from '$lib/api/client';

let navigations: string[];
let restoreNavigate: () => void;

beforeEach(() => {
  navigations = [];
  restoreNavigate = setNavigateForTesting((url) => navigations.push(url));
});

afterEach(() => {
  restoreNavigate();
  vi.unstubAllGlobals();
});

function respond(status: number, body: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response(JSON.stringify(body), { status })),
  );
}

describe('feedback submit rides the boundary', () => {
  it('an ordinary rejection keeps the dialog open with the server message inline', async () => {
    respond(400, { message: 'Message is required.' });
    openFeedback();

    const ok = await submitFeedback('bug', 'x');

    expect(ok).toBe(false);
    expect(feedbackOpen()).toBe(true);
    expect(feedbackError()).toBe('Message is required.');
    expect(navigations).toEqual([]);
  });

  it('a dead cookie takes the shared login redirect (no "reload and try again" lie)', async () => {
    respond(401, { message: 'unauthorized' });
    openFeedback();

    const outcome = await Promise.race([
      submitFeedback('bug', 'the drag broke').then(
        () => 'settled',
        () => 'rejected',
      ),
      new Promise((resolve) => setTimeout(() => resolve('pending'), 25)),
    ]);

    expect(outcome).toBe('pending');
    expect(navigations).toEqual([LOGIN_URL]);
  });
});
