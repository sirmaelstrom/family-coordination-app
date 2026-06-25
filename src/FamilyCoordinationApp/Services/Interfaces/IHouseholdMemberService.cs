using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

/// <summary>
/// Household member (whitelist) management — lifted out of the direct-EF <c>WhitelistAdmin.razor</c> so the
/// safety rules are testable and the HouseholdId scope (M1) is enforced server-side rather than only hidden in
/// the UI (review R-A2). Every method is household-scoped; the cross-household collision check on add is the one
/// intentional cross-tenant read (review R-A4 — it leaks no data, only a 409).
/// </summary>
public interface IHouseholdMemberService
{
    /// <summary>The household's users, ordered by email (parity WhitelistAdmin LoadUsers).</summary>
    Task<List<User>> GetMembersAsync(int householdId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a member by email (parity WhitelistAdmin.AddUser): normalize lower; another household ⇒
    /// <see cref="AddMemberOutcome.OtherHousehold"/>; existing-disabled ⇒ re-enable; existing-active ⇒ no-op;
    /// else create a whitelisted user (DisplayName = email local-part, GoogleId = null, CreatedAt = UtcNow).
    /// </summary>
    Task<AddMemberResult> AddMemberAsync(int householdId, string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enable/disable a member. Server-enforced guards (R-A2): toggling SELF ⇒ <see cref="MemberMutationResult.SelfForbidden"/>;
    /// disabling the LAST ACTIVE member ⇒ <see cref="MemberMutationResult.LastActiveForbidden"/>; unknown ⇒
    /// <see cref="MemberMutationResult.NotFound"/>. Returns the updated user on success.
    /// </summary>
    Task<(MemberMutationResult Result, User? User)> SetWhitelistAsync(
        int householdId, int currentUserId, int targetUserId, bool isWhitelisted, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a member. Server-enforced guards (R-A2): deleting SELF ⇒ <see cref="MemberMutationResult.SelfForbidden"/>;
    /// deleting the LAST user ⇒ <see cref="MemberMutationResult.LastUserForbidden"/>; unknown ⇒
    /// <see cref="MemberMutationResult.NotFound"/>. FK ON DELETE SET NULL keeps their recipes/feedback.
    /// </summary>
    Task<MemberMutationResult> DeleteMemberAsync(
        int householdId, int currentUserId, int targetUserId, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of <see cref="IHouseholdMemberService.AddMemberAsync"/>.</summary>
public enum AddMemberOutcome
{
    /// <summary>A brand-new whitelisted user was created.</summary>
    Created,
    /// <summary>An existing disabled user in this household was re-enabled.</summary>
    Reenabled,
    /// <summary>The email is already an active member of this household (no change).</summary>
    AlreadyActive,
    /// <summary>The email belongs to ANOTHER household — rejected (maps to 409, no data leak).</summary>
    OtherHousehold,
}

/// <summary>The added/affected member (null for <see cref="AddMemberOutcome.OtherHousehold"/>).</summary>
public sealed record AddMemberResult(AddMemberOutcome Outcome, User? User);

/// <summary>Outcome of a member toggle/delete — maps to HTTP in the endpoint (Self ⇒ 400; Last*/Blocked ⇒ 409; NotFound ⇒ 404).</summary>
public enum MemberMutationResult
{
    Ok,
    NotFound,
    SelfForbidden,
    LastActiveForbidden,
    LastUserForbidden,

    /// <summary>
    /// Delete refused by the database (a RESTRICT FK — e.g. the member has chore completions). Parity: the old
    /// WhitelistAdmin caught this and surfaced a generic error rather than deleting (FK ON DELETE SET NULL only
    /// covers recipes/feedback, not completions). Maps to 409 — disable the member instead.
    /// </summary>
    Blocked,
}
