using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Household member management (strangler — lifts WhitelistAdmin.razor's direct-EF logic into a testable,
/// M1-scoped service). Short-lived contexts via the factory (retires the page's long-lived <c>_context</c>
/// anti-pattern, review R-A6). The self / last-active / last-user guards are enforced HERE (server-side),
/// not just hidden in the UI (R-A2).
/// </summary>
public sealed class HouseholdMemberService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<HouseholdMemberService> logger) : IHouseholdMemberService
{
    public async Task<List<User>> GetMembersAsync(int householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await context.Users
            .Where(u => u.HouseholdId == householdId)
            .OrderBy(u => u.Email)
            .ToListAsync(cancellationToken);
    }

    public async Task<AddMemberResult> AddMemberAsync(int householdId, string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Intentional cross-household read (R-A4): the email belonging to ANOTHER household is rejected — this
        // takes precedence (parity WhitelistAdmin.AddUser:181). It returns no data, only the rejection outcome.
        var inOtherHousehold = await context.Users
            .AnyAsync(u => u.Email == normalized && u.HouseholdId != householdId, cancellationToken);
        if (inOtherHousehold)
        {
            return new AddMemberResult(AddMemberOutcome.OtherHousehold, null);
        }

        var existing = await context.Users
            .FirstOrDefaultAsync(u => u.Email == normalized && u.HouseholdId == householdId, cancellationToken);

        if (existing is not null)
        {
            if (existing.IsWhitelisted)
            {
                return new AddMemberResult(AddMemberOutcome.AlreadyActive, existing);
            }

            existing.IsWhitelisted = true;
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Re-enabled member {Email} in household {HouseholdId}", normalized, householdId);
            return new AddMemberResult(AddMemberOutcome.Reenabled, existing);
        }

        var created = new User
        {
            HouseholdId = householdId,
            Email = normalized,
            DisplayName = normalized.Split('@')[0],
            GoogleId = null, // set when the user first logs in with Google (parity)
            IsWhitelisted = true,
            CreatedAt = DateTime.UtcNow,
        };
        context.Users.Add(created);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Added member {Email} to household {HouseholdId}", normalized, householdId);
        return new AddMemberResult(AddMemberOutcome.Created, created);
    }

    public async Task<(MemberMutationResult Result, User? User)> SetWhitelistAsync(
        int householdId, int currentUserId, int targetUserId, bool isWhitelisted, CancellationToken cancellationToken = default)
    {
        if (targetUserId == currentUserId)
        {
            return (MemberMutationResult.SelfForbidden, null);
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var target = await context.Users
            .FirstOrDefaultAsync(u => u.HouseholdId == householdId && u.Id == targetUserId, cancellationToken);
        if (target is null)
        {
            return (MemberMutationResult.NotFound, null);
        }

        // Disabling the last active member would lock everyone out (parity: the page hides Disable for the
        // last active member; we enforce it server-side).
        if (!isWhitelisted && target.IsWhitelisted)
        {
            var activeCount = await context.Users
                .CountAsync(u => u.HouseholdId == householdId && u.IsWhitelisted, cancellationToken);
            if (activeCount <= 1)
            {
                return (MemberMutationResult.LastActiveForbidden, null);
            }
        }

        target.IsWhitelisted = isWhitelisted;
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "{Action} member {UserId} in household {HouseholdId}", isWhitelisted ? "Enabled" : "Disabled", targetUserId, householdId);
        return (MemberMutationResult.Ok, target);
    }

    public async Task<MemberMutationResult> DeleteMemberAsync(
        int householdId, int currentUserId, int targetUserId, CancellationToken cancellationToken = default)
    {
        if (targetUserId == currentUserId)
        {
            return MemberMutationResult.SelfForbidden;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var target = await context.Users
            .FirstOrDefaultAsync(u => u.HouseholdId == householdId && u.Id == targetUserId, cancellationToken);
        if (target is null)
        {
            return MemberMutationResult.NotFound;
        }

        var totalCount = await context.Users
            .CountAsync(u => u.HouseholdId == householdId, cancellationToken);
        if (totalCount <= 1)
        {
            return MemberMutationResult.LastUserForbidden;
        }

        context.Users.Remove(target);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A RESTRICT FK (e.g. ChoreCompletions.CompletedByUserId) blocks the delete. Parity: don't 500 —
            // surface a clean "can't delete" (the island toasts it). FK SET NULL only covers recipes/feedback.
            logger.LogWarning(ex, "Blocked deleting member {UserId} in household {HouseholdId} (FK reference)", targetUserId, householdId);
            return MemberMutationResult.Blocked;
        }
        logger.LogInformation("Deleted member {UserId} from household {HouseholdId}", targetUserId, householdId);
        return MemberMutationResult.Ok;
    }
}
