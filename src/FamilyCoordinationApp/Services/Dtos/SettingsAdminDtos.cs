namespace FamilyCoordinationApp.Services.Dtos;

using FamilyCoordinationApp.Data.Entities;

// ─────────────────────────────────────────────────────────────────────────
// Settings island C (Admin) DTOs — Household requests (site-admin-only) + Feedback
// (dual-mode) (strangler; mirrors the cluster-A/B M9 lockstep). Source of truth for
// the island TS contract:
//   tests/.../Fixtures/Settings/{household-requests,feedback}.json
//   + frontend/admin/src/lib/types.ts.
// A shape/casing change updates THIS file, those fixtures, and types.ts in lockstep
// (HouseholdRequestsDtoContractTests / FeedbackListDtoContractTests are the tripwires).
//
// ⚠ CASING: all keys camelCase (JsonSerializerDefaults.Web).
// ⚠ ENUMS (review R-C10): HouseholdRequestStatus + FeedbackType are REAL enums → the
//   app's global JsonStringEnumConverter(CamelCase) serializes them as camelCase
//   strings on the wire ("pending"/"approved"/"rejected", "bug"/"featureRequest"/
//   "general"). The island carries matching string-union types + a label/icon/color map.
// ⚠ DATES (review X5): RequestedAt / ReviewedAt / CreatedAt are FULL INSTANTS WITH
//   time-of-day (NOT the noon-UTC date-only case). They are projected server-side to
//   round-trip ISO-8601 UTC strings ("2026-06-24T18:30:00Z") and the island renders
//   them local via new Date(iso).toLocaleString(). Carried as `string` (not DateTime)
//   so the wire format is pinned explicitly and unambiguously UTC — see
//   SettingsAdminEndpoints.ToIso.
//
// Type names are admin-scoped and were grepped collision-free in the shared Dtos
// namespace (cluster-B hit ConnectedHouseholdDto already living in RecipeDtos.cs).
// ─────────────────────────────────────────────────────────────────────────

// ─── Household requests (site-admin-only) ───────────────────────────────────────

/// <summary>
/// The Household-requests view aggregate (parity <c>HouseholdAdmin.razor</c> LoadData): every household-creation
/// request (pending-first ordering) + every existing household with its member count. These are SITE-ADMIN GLOBAL
/// reads — <see cref="HouseholdRequest"/> has no HouseholdId and the list spans all tenants, gated by the 403
/// site-admin check, not by M1 (review R-C8).
/// </summary>
public sealed record HouseholdRequestsDto(
    IReadOnlyList<HouseholdRequestDto> Requests,
    IReadOnlyList<HouseholdSummaryDto> Households);

/// <summary>
/// One household-creation request. <see cref="Status"/> is a real enum → camelCase string on the wire.
/// <see cref="RequestedAt"/> is always present; <see cref="ReviewedAt"/>/<see cref="ReviewedBy"/>/
/// <see cref="RejectionReason"/> are null until a request is approved/rejected.
/// </summary>
public sealed record HouseholdRequestDto(
    int Id,
    string HouseholdName,
    string DisplayName,
    string Email,
    HouseholdRequestStatus Status,
    string RequestedAt,
    string? ReviewedAt,
    string? ReviewedBy,
    string? RejectionReason);

/// <summary>One existing household + its member count (parity: the "Existing Households" table; review R-C8 —
/// <see cref="MemberCount"/> = Households.Include(Users) → Users.Count).</summary>
public sealed record HouseholdSummaryDto(
    int HouseholdId,
    string Name,
    int MemberCount,
    string CreatedAt);

// ─── Feedback (dual-mode) ───────────────────────────────────────────────────────

/// <summary>
/// The Feedback view payload. <see cref="IsSiteAdmin"/> is the island's visibility signal (review R-C4): a site
/// admin sees ALL households' feedback; a regular user sees only their own household's (server-scoped, M1). The
/// island uses the flag for copy/affordances, NOT to decide what to fetch — the server already scoped it.
/// </summary>
public sealed record FeedbackListDto(
    bool IsSiteAdmin,
    IReadOnlyList<FeedbackDto> Items);

/// <summary>
/// One feedback item. <see cref="Type"/> is a real enum → camelCase string on the wire (review R-C10). Author is
/// a 3-way mapping (review R-C6): a live user → (<see cref="AuthorName"/>=DisplayName, <see cref="AuthorDeleted"/>
/// =false); a deleted user (UserId set, no row) → (null, true) → the island renders "Deleted user"; anonymous
/// (no UserId) → (null, false) → no author line at all.
/// </summary>
public sealed record FeedbackDto(
    int Id,
    FeedbackType Type,
    string Message,
    string? CurrentPage,
    bool IsRead,
    bool IsResolved,
    string CreatedAt,
    string? AuthorName,
    bool AuthorDeleted);
