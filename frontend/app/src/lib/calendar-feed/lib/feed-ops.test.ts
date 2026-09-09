import { describe, it, expect } from 'vitest';
import {
  copyableUrl,
  describeCreatedAt,
  phaseAfterRevoke,
  phaseFromCreated,
  phaseFromStatus,
  postIsRotate,
  primaryActionLabel,
  statusLine,
} from './feed-ops';

// ─────────────────────────────────────────────────────────────────────────
// The copy-once affordance (quest 17e38a88). The server returns the feed URL
// on POST only and stores a hash, so the card may show a copyable URL exactly
// once — between the POST response and the next status load or revoke. These
// tests pin that rule at the transition level, where it lives.
// ─────────────────────────────────────────────────────────────────────────

const NOW = new Date('2026-09-09T12:00:00Z');
const CREATED = { token: 'abc', url: 'https://family.example/api/calendar/meal-plan.ics?token=abc' };

describe('copy-once: the URL exists only between the POST response and the next load', () => {
  it('a status load never yields a copyable URL, even when a token is active', () => {
    expect(copyableUrl(phaseFromStatus({ active: false }))).toBeNull();
    expect(copyableUrl(phaseFromStatus({ active: true, createdAt: '2026-09-08T20:00:00Z' }))).toBeNull();
  });

  it('the POST response yields the URL exactly as returned', () => {
    const phase = phaseFromCreated(CREATED, NOW);
    expect(phase.kind).toBe('revealed');
    expect(copyableUrl(phase)).toBe(CREATED.url);
  });

  it('a status load AFTER the reveal forgets the URL — it cannot be re-read', () => {
    phaseFromCreated(CREATED, NOW); // revealed…
    const after = phaseFromStatus({ active: true, createdAt: NOW.toISOString() }); // …then any reload
    expect(after.kind).toBe('active');
    expect(copyableUrl(after)).toBeNull();
  });

  it('revoke forgets the URL and the token', () => {
    expect(phaseAfterRevoke()).toEqual({ kind: 'none' });
    expect(copyableUrl(phaseAfterRevoke())).toBeNull();
  });

  it('CONTROL: the revealed phase is the only one that carries a url field at all', () => {
    expect('url' in phaseFromStatus({ active: true })).toBe(false);
    expect('url' in phaseAfterRevoke()).toBe(false);
    expect('url' in phaseFromCreated(CREATED, NOW)).toBe(true);
  });
});

describe('rotate vs create', () => {
  it('POST is a rotate whenever a token is active — including right after a reveal', () => {
    expect(postIsRotate({ kind: 'none' })).toBe(false);
    expect(postIsRotate({ kind: 'active', createdAt: null })).toBe(true);
    expect(postIsRotate(phaseFromCreated(CREATED, NOW))).toBe(true);
  });

  it('the primary label says which one the user is about to do', () => {
    expect(primaryActionLabel({ kind: 'none' })).toBe('Create subscription link');
    expect(primaryActionLabel({ kind: 'active', createdAt: null })).toBe('Rotate link');
  });
});

describe('status copy tells the user the link is not re-readable', () => {
  it('the active line says it is shown once and that rotating kills the old link', () => {
    const line = statusLine({ kind: 'active', createdAt: null });
    expect(line).toMatch(/shown only once/);
    expect(line).toMatch(/old link stops working/);
  });

  it('the revealed line says to copy now and warns about sharing', () => {
    const line = statusLine(phaseFromCreated(CREATED, NOW));
    expect(line).toMatch(/will not be shown again/);
    expect(line).toMatch(/Anyone with the link/);
  });
});

describe('describeCreatedAt', () => {
  it('renders nothing for a missing or unparseable instant', () => {
    expect(describeCreatedAt(null)).toBe('');
    expect(describeCreatedAt('not a date')).toBe('');
  });

  it('distinguishes today from an earlier day (local rendering, never a bare date string)', () => {
    expect(describeCreatedAt(NOW.toISOString(), NOW)).toMatch(/^Created today at /);
    expect(describeCreatedAt('2026-09-01T12:00:00Z', NOW)).toMatch(/^Created /);
    expect(describeCreatedAt('2026-09-01T12:00:00Z', NOW)).not.toMatch(/today/);
  });
});
