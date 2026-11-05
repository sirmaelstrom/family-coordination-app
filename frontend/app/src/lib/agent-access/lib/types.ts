// ─────────────────────────────────────────────────────────────────────────
// TS mirror of /api/agent-tokens (quest 8bfa42cf). Source of truth:
//   - Endpoints/AgentTokenEndpoints.cs (anonymous-object responses)
//   - tests/.../Integration/AgentTokenTests.cs
//
// ⚠ SHOW-ONCE: the secret (`token`) is in the mint response only. The list never
//   carries it, nor its hash. The page must not keep it beyond the reveal.
// ⚠ DATES: every instant is a full ISO-8601 UTC string; render with new Date(iso).
// ─────────────────────────────────────────────────────────────────────────

import type { ShellContext as AppShellContext } from '$lib/session.svelte';

export type ShellContext = AppShellContext;

export type AgentAccess = 'read' | 'readWrite';
export type AgentTokenStatus = 'active' | 'expired' | 'revoked';

export interface AgentTokenDto {
  id: number;
  label: string;
  /** The first 12 characters of the secret (`fca_pat_` + 4), safe to show. */
  safePrefix: string;
  access: AgentAccess;
  createdAt: string;
  expiresAt: string;
  revokedAt: string | null;
  lastUsedAt: string | null;
  status: AgentTokenStatus;
}

export interface AgentTokenListDto {
  householdName: string | null;
  expiryPresetDays: number[];
  maxLifetimeDays: number;
  tokens: AgentTokenDto[];
}

/** Exactly one of `expiresInDays` / `expiresOn` (a `YYYY-MM-DD` local date). */
export interface MintAgentTokenBody {
  label: string;
  access: AgentAccess;
  expiresInDays?: number;
  expiresOn?: string;
}

export interface MintedAgentTokenDto {
  id: number;
  token: string;
  label: string;
  safePrefix: string;
  access: AgentAccess;
  createdAt: string;
  expiresAt: string;
}
