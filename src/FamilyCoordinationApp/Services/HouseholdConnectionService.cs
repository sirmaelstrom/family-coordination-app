using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class HouseholdConnectionService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<HouseholdConnectionService> logger) : IHouseholdConnectionService
{
    private const string InviteCharset = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int InviteCodeLength = 6;
    private const int MaxCodeRetries = 10;
    private static readonly TimeSpan DefaultExpiry = TimeSpan.FromHours(48);

    // Simple rate limiting: householdId -> (failCount, windowStart)
    private static readonly ConcurrentDictionary<int, (int Count, DateTime WindowStart)> FailedAttempts = new();
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromHours(1);

    public async Task<HouseholdInvite> GenerateInviteAsync(
        int householdId, int userId, TimeSpan? validFor = null, CancellationToken cancellationToken = default)
    {
        var expiry = validFor ?? DefaultExpiry;

        // Invalidate any existing active invites for this household
        await InvalidateInviteAsync(householdId, cancellationToken);

        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxCodeRetries; attempt++)
        {
            try
            {
                await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

                var code = GenerateInviteCode();
                var invite = new HouseholdInvite
                {
                    HouseholdId = householdId,
                    InviteCode = code,
                    CreatedByUserId = userId,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.Add(expiry),
                    IsUsed = false
                };

                context.HouseholdInvites.Add(invite);
                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation(
                    "Generated invite code for household {HouseholdId} by user {UserId}, expires at {ExpiresAt}",
                    householdId, userId, invite.ExpiresAt);

                return invite;
            }
            catch (DbUpdateException ex) when (attempt < MaxCodeRetries)
            {
                lastException = ex;
                logger.LogWarning(
                    "Invite code collision for household {HouseholdId} (attempt {Attempt}/{MaxRetries}). Retrying.",
                    householdId, attempt, MaxCodeRetries);
            }
        }

        logger.LogError(lastException,
            "Failed to generate unique invite code for household {HouseholdId} after {MaxRetries} attempts",
            householdId, MaxCodeRetries);

        throw new InvalidOperationException(
            $"Failed to generate unique invite code after {MaxCodeRetries} attempts.",
            lastException);
    }

    public async Task<HouseholdInvite?> GetActiveInviteAsync(int householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.HouseholdInvites
            .Where(i => i.HouseholdId == householdId && !i.IsUsed && i.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task InvalidateInviteAsync(int householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var activeInvites = await context.HouseholdInvites
            .Where(i => i.HouseholdId == householdId && !i.IsUsed && i.ExpiresAt > DateTime.UtcNow)
            .ToListAsync(cancellationToken);

        foreach (var invite in activeInvites)
        {
            invite.IsUsed = true;
            invite.UsedAt = DateTime.UtcNow;
        }

        if (activeInvites.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Invalidated {Count} active invite(s) for household {HouseholdId}",
                activeInvites.Count, householdId);
        }
    }

    public async Task<(bool IsValid, string? HouseholdName, string? Error)> ValidateInviteCodeAsync(
        string code, int acceptingHouseholdId, CancellationToken cancellationToken = default)
    {
        var normalizedCode = code.Trim().ToUpperInvariant();

        // Rate limiting check
        if (IsRateLimited(acceptingHouseholdId))
        {
            return (false, null, "Too many failed attempts. Please try again later.");
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var invite = await context.HouseholdInvites
            .Include(i => i.Household)
            .FirstOrDefaultAsync(i => i.InviteCode == normalizedCode, cancellationToken);

        if (invite == null)
        {
            RecordFailedAttempt(acceptingHouseholdId);
            return (false, null, "Invalid invite code.");
        }

        if (invite.IsUsed)
        {
            RecordFailedAttempt(acceptingHouseholdId);
            return (false, null, "This invite code has already been used.");
        }

        if (invite.ExpiresAt <= DateTime.UtcNow)
        {
            RecordFailedAttempt(acceptingHouseholdId);
            return (false, null, "This invite code has expired.");
        }

        // Self-connection guard
        if (invite.HouseholdId == acceptingHouseholdId)
        {
            RecordFailedAttempt(acceptingHouseholdId);
            return (false, null, "You cannot connect to your own household.");
        }

        // Already connected check
        if (await AreHouseholdsConnectedInternalAsync(context, invite.HouseholdId, acceptingHouseholdId, cancellationToken))
        {
            RecordFailedAttempt(acceptingHouseholdId);
            return (false, null, "Your households are already connected.");
        }

        // Reset failed attempts on successful validation
        ResetFailedAttempts(acceptingHouseholdId);

        return (true, invite.Household.Name, null);
    }

    public async Task<(bool Success, string? ConnectedHouseholdName, string? Error)> AcceptInviteAsync(
        string code, int acceptingHouseholdId, int userId, CancellationToken cancellationToken = default)
    {
        var normalizedCode = code.Trim().ToUpperInvariant();

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // Validate the invite
            var invite = await context.HouseholdInvites
                .Include(i => i.Household)
                .FirstOrDefaultAsync(i => i.InviteCode == normalizedCode, cancellationToken);

            if (invite == null)
                return (false, null, "Invalid invite code.");

            if (invite.IsUsed)
                return (false, null, "This invite code has already been used.");

            if (invite.ExpiresAt <= DateTime.UtcNow)
                return (false, null, "This invite code has expired.");

            if (invite.HouseholdId == acceptingHouseholdId)
                return (false, null, "You cannot connect to your own household.");

            if (await AreHouseholdsConnectedInternalAsync(context, invite.HouseholdId, acceptingHouseholdId, cancellationToken))
                return (false, null, "Your households are already connected.");

            // Mark invite as used
            invite.IsUsed = true;
            invite.UsedAt = DateTime.UtcNow;
            invite.UsedByHouseholdId = acceptingHouseholdId;
            invite.UsedByUserId = userId;

            // Create connection with ordered IDs
            var id1 = Math.Min(invite.HouseholdId, acceptingHouseholdId);
            var id2 = Math.Max(invite.HouseholdId, acceptingHouseholdId);

            var connection = new HouseholdConnection
            {
                HouseholdId1 = id1,
                HouseholdId2 = id2,
                ConnectedAt = DateTime.UtcNow,
                InitiatedByUserId = invite.CreatedByUserId,
                AcceptedByUserId = userId
            };

            context.HouseholdConnections.Add(connection);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Reset failed attempts on success
            ResetFailedAttempts(acceptingHouseholdId);

            logger.LogInformation(
                "Household {AcceptingHouseholdId} connected to household {InvitingHouseholdId} via invite code",
                acceptingHouseholdId, invite.HouseholdId);

            return (true, invite.Household.Name, null);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogError(ex, "Failed to accept invite code for household {HouseholdId}", acceptingHouseholdId);
            throw;
        }
    }

    public async Task<List<ConnectedHouseholdInfo>> GetConnectedHouseholdsAsync(
        int householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var connections = await context.HouseholdConnections
            .Where(c => c.HouseholdId1 == householdId || c.HouseholdId2 == householdId)
            .Include(c => c.Household1)
            .Include(c => c.Household2)
            .ToListAsync(cancellationToken);

        return connections.Select(c =>
        {
            var otherHousehold = c.HouseholdId1 == householdId ? c.Household2 : c.Household1;
            return new ConnectedHouseholdInfo(otherHousehold.Id, otherHousehold.Name, c.ConnectedAt);
        }).ToList();
    }

    public async Task<bool> AreHouseholdsConnectedAsync(int id1, int id2, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await AreHouseholdsConnectedInternalAsync(context, id1, id2, cancellationToken);
    }

    public async Task DisconnectHouseholdsAsync(int id1, int id2, CancellationToken cancellationToken = default)
    {
        var minId = Math.Min(id1, id2);
        var maxId = Math.Max(id1, id2);

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var connection = await context.HouseholdConnections
            .FirstOrDefaultAsync(c => c.HouseholdId1 == minId && c.HouseholdId2 == maxId, cancellationToken);

        if (connection != null)
        {
            context.HouseholdConnections.Remove(connection);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Disconnected households {Id1} and {Id2}", minId, maxId);
        }
    }

    public async Task<int> CleanupExpiredInvitesAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var cutoff = DateTime.UtcNow.AddDays(-7);
        var expiredInvites = await context.HouseholdInvites
            .Where(i => i.ExpiresAt < DateTime.UtcNow && i.CreatedAt < cutoff)
            .ToListAsync(cancellationToken);

        if (expiredInvites.Count > 0)
        {
            context.HouseholdInvites.RemoveRange(expiredInvites);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Cleaned up {Count} expired invite(s)", expiredInvites.Count);
        }

        return expiredInvites.Count;
    }

    private static string GenerateInviteCode()
    {
        return string.Create(InviteCodeLength, InviteCharset, (span, charset) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = charset[Random.Shared.Next(charset.Length)];
            }
        });
    }

    private static async Task<bool> AreHouseholdsConnectedInternalAsync(
        ApplicationDbContext context, int id1, int id2, CancellationToken cancellationToken)
    {
        var minId = Math.Min(id1, id2);
        var maxId = Math.Max(id1, id2);

        return await context.HouseholdConnections
            .AnyAsync(c => c.HouseholdId1 == minId && c.HouseholdId2 == maxId, cancellationToken);
    }

    private static bool IsRateLimited(int householdId)
    {
        if (!FailedAttempts.TryGetValue(householdId, out var entry))
            return false;

        // Reset if window has expired
        if (DateTime.UtcNow - entry.WindowStart > RateLimitWindow)
        {
            FailedAttempts.TryRemove(householdId, out _);
            return false;
        }

        return entry.Count >= MaxFailedAttempts;
    }

    private static void RecordFailedAttempt(int householdId)
    {
        FailedAttempts.AddOrUpdate(
            householdId,
            _ => (1, DateTime.UtcNow),
            (_, existing) =>
            {
                // Reset window if expired
                if (DateTime.UtcNow - existing.WindowStart > RateLimitWindow)
                    return (1, DateTime.UtcNow);

                return (existing.Count + 1, existing.WindowStart);
            });
    }

    private static void ResetFailedAttempts(int householdId)
    {
        FailedAttempts.TryRemove(householdId, out _);
    }

    /// <summary>
    /// Exposed for testing — clears rate limiting state.
    /// </summary>
    internal static void ClearRateLimitState()
    {
        FailedAttempts.Clear();
    }
}
