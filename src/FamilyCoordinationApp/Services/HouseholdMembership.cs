using FamilyCoordinationApp.Data;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// The one membership check for a user id a command receives from the client (quest 82fc7f54): an assignee, an
/// owner, a roster member, a hand-off target, a completion participant. The tenancy write step checks
/// <c>HouseholdId</c> only, not user references, so without this another household's user id would be stored.
/// <para>Runs on the caller's own context: the <c>"Tenant"</c> filter scopes <c>Users</c> to the caller's household,
/// so another household's user finds nothing, and the check reads the same snapshot the command then writes.</para>
/// <list type="bullet">
/// <item><see cref="EnsureMemberAsync"/> — the user belongs to the household. For a reference to someone already on
/// the row (a completion participant, a roster member being removed, an owner the edit leaves unchanged).</item>
/// <item><see cref="EnsureAssignableAsync"/> — a member AND enabled (<c>IsWhitelisted</c>). For a NEW assignment:
/// a disabled member can't be given work, but an existing assignment is not rewritten.</item>
/// </list>
/// Both throw <see cref="HouseholdMembershipException"/> (a <see cref="DomainValidationException"/>, so the /api
/// exception branch answers 400 with its message).
/// </summary>
public static class HouseholdMembership
{
    public static async Task EnsureMemberAsync(ApplicationDbContext context, int householdId, int userId, CancellationToken cancellationToken)
    {
        if (!await context.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            throw HouseholdMembershipException.NotAMember(userId, householdId);
        }
    }

    public static async Task EnsureAssignableAsync(ApplicationDbContext context, int householdId, int userId, CancellationToken cancellationToken)
    {
        var member = await context.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsWhitelisted })
            .FirstOrDefaultAsync(cancellationToken);

        if (member is null)
        {
            throw HouseholdMembershipException.NotAMember(userId, householdId);
        }

        if (!member.IsWhitelisted)
        {
            throw new HouseholdMembershipException($"User {userId} is disabled in household {householdId} and can't be assigned.");
        }
    }
}

/// <summary>A user id that isn't a member of the caller's household, or isn't assignable there (400).</summary>
public sealed class HouseholdMembershipException : DomainValidationException
{
    public HouseholdMembershipException(string message) : base(message)
    {
    }

    internal static HouseholdMembershipException NotAMember(int userId, int householdId) =>
        new($"User {userId} is not a member of household {householdId}.");
}
