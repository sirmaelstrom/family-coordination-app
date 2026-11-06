// Pure helpers for the Agent access page (quest 8bfa42cf): the mint body from the form, and the copy for each row.
import type { AgentAccess, AgentTokenDto, MintAgentTokenBody } from './types';

/** The form's expiry choice: a preset number of days, or a custom local date. */
export type ExpiryChoice = { kind: 'days'; days: number } | { kind: 'date'; date: string };

export const DEFAULT_EXPIRY_DAYS = 365;

/** The page that re-proves the member before a mint (the server answers a stale sign-in with 403). */
export function reauthUrl(returnPath = '/settings/agent-access'): string {
  return `/account/login?Reauth=true&ReturnUrl=${encodeURIComponent(returnPath)}`;
}

/** Build the mint body, or a message saying what to fix. Never sends both expiry fields. */
export function mintBody(
  label: string,
  access: AgentAccess,
  expiry: ExpiryChoice,
): { body: MintAgentTokenBody } | { error: string } {
  const trimmed = label.trim();
  if (!trimmed) return { error: 'Give the token a name, such as the client that will use it.' };
  if (trimmed.length > 100) return { error: 'The name can be at most 100 characters.' };
  if (expiry.kind === 'days') return { body: { label: trimmed, access, expiresInDays: expiry.days } };
  if (!/^\d{4}-\d{2}-\d{2}$/.test(expiry.date)) return { error: 'Pick an expiry date.' };
  return { body: { label: trimmed, access, expiresOn: expiry.date } };
}

export function accessLabel(access: AgentAccess): string {
  return access === 'readWrite' ? 'Read and write' : 'Read only';
}

/** "12 Nov 2027" in the viewer's locale, from a full ISO instant. */
export function formatDate(iso: string | null, locale?: string): string {
  if (!iso) return '';
  return new Date(iso).toLocaleDateString(locale, { year: 'numeric', month: 'short', day: 'numeric' });
}

/** One line under the token's name: its state, expiry and last use. */
export function describeToken(token: AgentTokenDto, locale?: string): string {
  const used = token.lastUsedAt ? `last used ${formatDate(token.lastUsedAt, locale)}` : 'never used';
  switch (token.status) {
    case 'revoked':
      return `Revoked ${formatDate(token.revokedAt, locale)} · ${used}`;
    case 'expired':
      return `Expired ${formatDate(token.expiresAt, locale)} · ${used}`;
    default:
      return `Expires ${formatDate(token.expiresAt, locale)} · ${used}`;
  }
}

/** Example client config for the revealed token. The secret goes in an env var, never in a file. */
export function clientSetup(origin: string): { client: string; text: string }[] {
  const url = `${origin}/mcp`;
  return [
    {
      client: 'Codex (~/.codex/config.toml)',
      text: `[mcp_servers.family]\nurl = "${url}"\nbearer_token_env_var = "FAMILYAPP_TOKEN"`,
    },
    {
      client: 'Claude Code',
      // Single quotes: Claude Code expands ${FAMILYAPP_TOKEN} when it connects, so the secret never lands in its config.
      text: `claude mcp add --transport http --scope user family ${url} --header 'Authorization: Bearer \${FAMILYAPP_TOKEN}'`,
    },
  ];
}
