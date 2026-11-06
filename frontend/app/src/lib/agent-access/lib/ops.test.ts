import { describe, expect, it } from 'vitest';
import { accessLabel, clientSetup, describeToken, mintBody, reauthUrl } from './ops';
import type { AgentTokenDto } from './types';

describe('mintBody', () => {
  it('sends a preset as expiresInDays only', () => {
    expect(mintBody('  Claude Code  ', 'readWrite', { kind: 'days', days: 365 })).toEqual({
      body: { label: 'Claude Code', access: 'readWrite', expiresInDays: 365 },
    });
  });

  it('sends a custom date as expiresOn only', () => {
    expect(mintBody('Codex', 'read', { kind: 'date', date: '2027-01-31' })).toEqual({
      body: { label: 'Codex', access: 'read', expiresOn: '2027-01-31' },
    });
  });

  it('refuses a blank name, an over-long name and a missing date', () => {
    expect(mintBody('   ', 'read', { kind: 'days', days: 30 })).toHaveProperty('error');
    expect(mintBody('x'.repeat(101), 'read', { kind: 'days', days: 30 })).toHaveProperty('error');
    expect(mintBody('Codex', 'read', { kind: 'date', date: '' })).toHaveProperty('error');
  });
});

describe('copy', () => {
  const base: AgentTokenDto = {
    id: 1,
    label: 'Claude Code',
    safePrefix: 'fca_pat_AbCd',
    access: 'read',
    createdAt: '2026-11-05T12:00:00Z',
    expiresAt: '2027-11-05T12:00:00Z',
    revokedAt: null,
    lastUsedAt: null,
    status: 'active',
  };

  it('says what each state means', () => {
    expect(describeToken(base, 'en-US')).toMatch(/^Expires .*2027 · never used$/);
    expect(describeToken({ ...base, status: 'revoked', revokedAt: '2026-11-06T12:00:00Z' }, 'en-US')).toMatch(/^Revoked /);
    expect(describeToken({ ...base, status: 'expired' }, 'en-US')).toMatch(/^Expired /);
    expect(describeToken({ ...base, lastUsedAt: '2026-11-06T12:00:00Z' }, 'en-US')).toMatch(/last used /);
    expect(accessLabel('read')).toBe('Read only');
    expect(accessLabel('readWrite')).toBe('Read and write');
  });

  it('the reauth link returns to this page and asks for a fresh sign-in', () => {
    expect(reauthUrl()).toBe('/account/login?Reauth=true&ReturnUrl=%2Fsettings%2Fagent-access');
  });

  it('client setup points at /mcp and keeps the secret in an environment variable', () => {
    const rows = clientSetup('https://family.example');
    expect(rows).toHaveLength(2);
    for (const row of rows) {
      expect(row.text).toContain('https://family.example/mcp');
      expect(row.text).toContain('FAMILYAPP_TOKEN');
      expect(row.text).not.toMatch(/fca_pat_/);
    }
  });
});
