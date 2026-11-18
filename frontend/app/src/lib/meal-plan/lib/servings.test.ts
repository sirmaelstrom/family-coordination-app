import { describe, it, expect } from 'vitest';
import { parseServingsInput, servingsChip, MAX_SERVINGS } from './servings';

describe('servingsChip', () => {
  it('shows a saved override even when the recipe has no yield (it must stay visible and clearable)', () => {
    // Land review blocker: a member set a meal to 20, then cleared the recipe's yield; the card hid the 20.
    expect(servingsChip(null, 20)?.text).toBe('for 20');
    expect(servingsChip(0, 20)?.text).toBe('for 20');
    expect(servingsChip(undefined, 20)?.label).toContain('no yield');
  });

  it('offers to start an override only when the recipe declares a yield', () => {
    expect(servingsChip(null, null)).toBeNull();
    expect(servingsChip(0, null)).toBeNull();
    expect(servingsChip(6, null)).toEqual({ text: 'serves 6', label: 'Recipe serves 6. Cook for a different number' });
  });

  it('shows the override against the yield when both are set', () => {
    expect(servingsChip(6, 20)).toEqual({ text: 'for 20', label: 'Cooking for 20; recipe serves 6. Change' });
  });
});

describe('parseServingsInput', () => {
  it('treats blank as "cook it as written", NOT as an abandoned edit', () => {
    // The council's Major on this PR: the dialog silently refused empty input, so the documented reset
    // was unreachable. This is the assertion that the empty case reaches the API as null.
    expect(parseServingsInput('')).toEqual({ ok: true, servings: null });
    expect(parseServingsInput('   ')).toEqual({ ok: true, servings: null });
  });

  it('accepts a positive whole number', () => {
    expect(parseServingsInput('8')).toEqual({ ok: true, servings: 8 });
    expect(parseServingsInput(' 12 ')).toEqual({ ok: true, servings: 12 });
    expect(parseServingsInput('1')).toEqual({ ok: true, servings: 1 });
  });

  it('rejects what would silently corrupt the scale factor', () => {
    // 0 would zero the list out; a negative would invert it; a fraction is not a number of people.
    for (const bad of ['0', '-3', '2.5', 'four', '1/2', 'NaN', 'Infinity']) {
      const result = parseServingsInput(bad);
      expect(result.ok, `"${bad}" must be rejected`).toBe(false);
    }
  });

  it('rejects a value past the cap the server also enforces', () => {
    expect(parseServingsInput(String(MAX_SERVINGS)).ok).toBe(true);
    expect(parseServingsInput(String(MAX_SERVINGS + 1)).ok).toBe(false);
  });
});
