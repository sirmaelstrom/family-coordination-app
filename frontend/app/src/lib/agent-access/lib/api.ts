import type { AgentTokenListDto, MintAgentTokenBody, MintedAgentTokenDto } from './types';
// Transport + error contract live in $lib/api/client (the one HTTP boundary, quest 79aa83e7).
import { ApiError, apiGet, apiSend } from '$lib/api/client';

export { ApiError };

const BASE = '/api/agent-tokens';

/** The caller's own tokens (never a secret or hash), plus the household name and expiry presets. */
export function listAgentTokens(): Promise<AgentTokenListDto> {
  return apiGet<AgentTokenListDto>(BASE);
}

/** Mint → the one response carrying the secret. 403 = sign in again first (fresh sign-in gate). */
export function mintAgentToken(body: MintAgentTokenBody): Promise<MintedAgentTokenDto> {
  return apiSend<MintedAgentTokenDto>(BASE, 'POST', body);
}

/** Revoke one of the caller's tokens → 204. Takes effect on the agent's next call. */
export async function revokeAgentToken(id: number): Promise<void> {
  await apiSend<void>(`${BASE}/${id}`, 'DELETE');
}
