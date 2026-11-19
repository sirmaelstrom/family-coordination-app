import { describe, expect, it } from 'vitest';
import { formatDeletedDate } from './dates';

// formatDeletedDate renders in the viewer's locale and timezone, so the checks are on what
// must hold in any of them: empty input, and the year showing only for another year.
describe('formatDeletedDate', () => {
  const now = new Date(2026, 10, 19, 12); // local noon, 19 Nov 2026

  it('is empty for a missing or unparseable instant', () => {
    expect(formatDeletedDate(null, now)).toBe('');
    expect(formatDeletedDate('', now)).toBe('');
    expect(formatDeletedDate('not a date', now)).toBe('');
  });

  it('leaves the year off a date in the current year', () => {
    const text = formatDeletedDate('2026-06-20T14:30:00Z', now);
    expect(text).not.toBe('');
    expect(text).not.toContain('2026');
  });

  it('shows the year of a date in another year', () => {
    expect(formatDeletedDate('2025-06-20T14:30:00Z', now)).toContain('2025');
  });
});
