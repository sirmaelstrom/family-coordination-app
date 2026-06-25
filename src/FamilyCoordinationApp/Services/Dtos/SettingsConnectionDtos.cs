namespace FamilyCoordinationApp.Services.Dtos;

// ─────────────────────────────────────────────────────────────────────────
// Settings island B (Connections) DTOs — the household-connection invite flow
// (strangler; mirrors the cluster-A SettingsDtos lockstep). Source of truth for
// the island TS contract:
//   tests/.../Fixtures/Settings/connections.json + frontend/connections/src/lib/types.ts.
// A shape/casing change updates THIS file, that fixture, and types.ts in
// lockstep (ConnectionsDtoContractTests is the tripwire).
//
// ⚠ CASING: all keys camelCase (JsonSerializerDefaults.Web).
// ⚠ DATES (review X5): ExpiresAt / ConnectedAt are FULL INSTANTS (DateTime, UTC) —
//   they serialize as round-trip ISO-8601 strings ("2026-06-26T12:00:00Z"), NOT
//   bare "YYYY-MM-DD". The island renders them local via new Date(iso) (relative
//   "expires in N hours" for the invite, absolute "Jun 26, 2026" for connected-at).
//   These are NOT the noon-UTC date-only case. No enums here ⇒ plain camelCase.
//
// Type names are connection-scoped (ConnectionInviteDto / ConnectedFamilyDto) to
// stay collision-free in the shared Dtos namespace — RecipeDtos already owns a
// ConnectedHouseholdDto(id, name) (no ConnectedAt), so this view uses its own. The
// JSON property names (code/expiresAt/…) are what the wire contract + types.ts
// mirror, not the C# type names.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// The Connections view aggregate (parity <c>Connections.razor</c> LoadData): the household's single active
/// invite (null when none) + the list of connected households. One GET payload over
/// <c>GetActiveInviteAsync</c> + <c>GetConnectedHouseholdsAsync</c>.
/// </summary>
public sealed record ConnectionsDto(
    ConnectionInviteDto? ActiveInvite,
    IReadOnlyList<ConnectedFamilyDto> Connected);

/// <summary>An active share code. <see cref="Code"/> is the entity's <c>InviteCode</c> (review R-B1, NOT a bare
/// <c>Code</c>); <see cref="ExpiresAt"/> is a UTC instant the island renders as relative "expires in …" text.</summary>
public sealed record ConnectionInviteDto(
    string Code,
    DateTime ExpiresAt);

/// <summary>One connected household. <see cref="ConnectedAt"/> is a UTC instant rendered as an absolute local date.</summary>
public sealed record ConnectedFamilyDto(
    int HouseholdId,
    string HouseholdName,
    DateTime ConnectedAt);

/// <summary>
/// The outcome of a validate call (200-with-outcome envelope, NOT a 4xx — review §8). An invalid / expired /
/// self / already-connected code is an expected user-flow result: <see cref="IsValid"/> is false and
/// <see cref="Error"/> carries the service's message (the island maps it to copy via mapValidationError, a
/// parity port that today passes the message through).
/// </summary>
public sealed record ValidateInviteResultDto(
    bool IsValid,
    string? HouseholdName,
    string? Error);

/// <summary>
/// The outcome of an accept call (200-with-outcome envelope). On success the island toasts
/// <see cref="ConnectedHouseholdName"/> and refreshes the connected list; on failure it returns to the entry
/// view showing <see cref="Error"/> (review R-B3).
/// </summary>
public sealed record AcceptInviteResultDto(
    bool Success,
    string? ConnectedHouseholdName,
    string? Error);
