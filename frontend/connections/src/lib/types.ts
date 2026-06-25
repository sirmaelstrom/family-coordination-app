// ─────────────────────────────────────────────────────────────────────────
// TS mirror of the /api/settings/connections contract (M9 lockstep). Source of truth:
//   - Services/Dtos/SettingsConnectionDtos.cs (the C# records — connection-scoped
//     names there to avoid colliding with the recipes ConnectedHouseholdDto; here
//     they are island-local)
//   - tests/.../Fixtures/Settings/connections.json (byte-locked tripwire)
//   - Endpoints/SettingsConnectionsEndpoints.cs (request DTOs)
//
// ⚠ CASING: all keys camelCase. No enums.
// ⚠ DATES (review X5): `expiresAt` / `connectedAt` are FULL ISO-8601 instants (UTC) —
//   render them local via new Date(iso) (NEVER new Date('YYYY-MM-DD')). See dates.ts.
// ─────────────────────────────────────────────────────────────────────────

/** The per-page context the Razor host hands the island via data- attributes. */
export interface ShellContext {
  householdId: number;
  userId: number;
  userName: string;
}

/** The household's single active invite. */
export interface InviteDto {
  code: string;
  /** full ISO-8601 instant (UTC). */
  expiresAt: string;
}

/** One connected household. */
export interface ConnectedDto {
  householdId: number;
  householdName: string;
  /** full ISO-8601 instant (UTC). */
  connectedAt: string;
}

/** The Connections view aggregate (GET /). `activeInvite` is null when none. */
export interface ConnectionsDto {
  activeInvite: InviteDto | null;
  connected: ConnectedDto[];
}

/** Validate outcome envelope (200, never a 4xx — review §8). */
export interface ValidateResultDto {
  isValid: boolean;
  householdName: string | null;
  error: string | null;
}

/** Accept outcome envelope (200). On failure the island returns to the entry view (review R-B3). */
export interface AcceptResultDto {
  success: boolean;
  connectedHouseholdName: string | null;
  error: string | null;
}
