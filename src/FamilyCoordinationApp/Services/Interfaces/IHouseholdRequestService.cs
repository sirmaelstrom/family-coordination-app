using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

/// <summary>
/// Household-creation request administration — lifted out of the direct-EF <c>HouseholdAdmin.razor</c> so the
/// load-bearing approval logic is testable (review: cluster C). These are SITE-ADMIN GLOBAL operations — the
/// site-admin 403 gate lives in the endpoint, NOT here (review R-C8 — requests have no HouseholdId and span all
/// tenants). Two correctness invariants are enforced HERE, server-side, not just hidden behind the page's buttons:
/// <list type="bullet">
/// <item><b>R-C2 (atomicity):</b> approve creates household + whitelisted user + marks the request approved +
/// seeds default categories inside ONE transaction — all four commit or none do (the old page did three separate
/// commits → a mid-failure left an orphan household or a household with no categories).</item>
/// <item><b>R-C3 (already-reviewed guard):</b> approve/reject of a non-<see cref="HouseholdRequestStatus.Pending"/>
/// request is refused (<see cref="ReviewOutcome.AlreadyReviewed"/> → 409), closing the latent duplicate-household
/// race two admins (or one on a 30s-stale view) could otherwise hit.</item>
/// </list>
/// </summary>
public interface IHouseholdRequestService
{
    /// <summary>
    /// Every household request (pending-first, then newest — parity ordering) + every existing household with its
    /// <see cref="Household.Users"/> populated (for the member count). A cross-tenant read, legitimately (R-C8).
    /// </summary>
    Task<HouseholdAdminData> GetDataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Approve a pending request inside one transaction (R-C2): create the household, create its first whitelisted
    /// user, mark the request Approved (ReviewedAt/ReviewedBy = now/<paramref name="reviewerEmail"/>), and seed the
    /// nine default categories. Non-pending ⇒ <see cref="ReviewOutcome.AlreadyReviewed"/> (R-C3); unknown id ⇒
    /// <see cref="ReviewOutcome.NotFound"/>. On success the created <see cref="Household"/> is returned (member
    /// count = 1) for the 201 summary.
    /// </summary>
    Task<ApproveResult> ApproveAsync(int requestId, string reviewerEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reject a pending request: mark Rejected + ReviewedAt/ReviewedBy + <paramref name="reason"/> (OPTIONAL —
    /// nullable/empty allowed, review R-C7). Non-pending ⇒ <see cref="ReviewOutcome.AlreadyReviewed"/> (R-C3);
    /// unknown id ⇒ <see cref="ReviewOutcome.NotFound"/>.
    /// </summary>
    Task<ReviewOutcome> RejectAsync(int requestId, string? reason, string reviewerEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin-initiated household creation (the "push" invite — the mirror of the self-request→approve "pull"). A
    /// site admin names a household and its owner's email; this creates the household + a whitelisted owner user
    /// (GoogleId null until their first Google login — parity with <c>HouseholdMemberService.AddMemberAsync</c>) so
    /// the owner lands straight in on first sign-in, no self-request. Household + owner + the nine default categories
    /// commit in ONE transaction (R-C2, same shape as <see cref="ApproveAsync"/>); the curated chore/room library is
    /// seeded post-commit (parity <c>SetupService.CreateHouseholdAsync</c>) so a pushed household is as complete as a
    /// first-run one. If the email already belongs to any user ⇒ <see cref="CreateHouseholdOutcome.EmailInUse"/>
    /// (they already have a household); blank name/email ⇒ <see cref="CreateHouseholdOutcome.InvalidInput"/>.
    /// </summary>
    Task<CreateHouseholdResult> CreateHouseholdAsync(
        string householdName, string ownerEmail, string? ownerDisplayName, string createdByEmail,
        CancellationToken cancellationToken = default);
}

/// <summary>The Household-requests view data (raw entities; the endpoint projects to DTOs).</summary>
public sealed record HouseholdAdminData(
    IReadOnlyList<HouseholdRequest> Requests,
    IReadOnlyList<Household> Households);

/// <summary>Outcome of an approve/reject — maps to HTTP in the endpoint (NotFound ⇒ 404, AlreadyReviewed/EmailInUse ⇒ 409).</summary>
public enum ReviewOutcome
{
    Ok,
    NotFound,
    AlreadyReviewed,

    /// <summary>
    /// Approve hit the unique <c>Users.Email</c> constraint: the request's email is already a user (a stale
    /// data state, or a concurrent approve that won the race and created the user first). The transaction rolled
    /// back fully — no orphan household. Maps to a clean 409 rather than letting the <c>DbUpdateException</c>
    /// surface as a 500 (council R1). Only approve can produce this.
    /// </summary>
    EmailInUse,
}

/// <summary>The result of an approve: the <see cref="Outcome"/> plus the created household (only when Ok).</summary>
public sealed record ApproveResult(ReviewOutcome Outcome, Household? CreatedHousehold);

/// <summary>Outcome of an admin-initiated household create — maps to HTTP (InvalidInput ⇒ 400, EmailInUse ⇒ 409).</summary>
public enum CreateHouseholdOutcome
{
    Ok,

    /// <summary>Household name or owner email was blank after trimming — a clean 400 (the endpoint also guards this).</summary>
    InvalidInput,

    /// <summary>
    /// The owner email already belongs to a user (any household). Creating them would collide with the unique
    /// <c>Users.Email</c> constraint and they already have a home — refuse with a clean 409, transaction rolled back.
    /// </summary>
    EmailInUse,
}

/// <summary>The result of a create: the <see cref="Outcome"/> plus the created household (only when Ok).</summary>
public sealed record CreateHouseholdResult(CreateHouseholdOutcome Outcome, Household? Household);
