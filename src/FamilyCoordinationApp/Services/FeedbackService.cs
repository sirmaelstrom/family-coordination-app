using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Feedback administration (strangler — lifts <c>FeedbackAdmin.razor</c>'s direct-EF logic into a testable,
/// dual-mode service). Short-lived contexts via the factory. Every read and mutation is household-scoped for a
/// non-admin (R-C1, the IDOR fix): the scope is part of the query, so a non-admin posting another household's id
/// finds nothing → the endpoint 404s with no existence leak. A site admin is unscoped (sees/acts on any item).
/// </summary>
public sealed class FeedbackService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<FeedbackService> logger) : IFeedbackService
{
    public async Task<IReadOnlyList<Feedback>> GetFeedbackAsync(bool isSiteAdmin, int? householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<Feedback> query = context.Feedbacks.Include(f => f.User);

        // Dual-mode: site admin → all households; regular user → own household only (R-C1, server-scoped). A
        // non-admin with no resolved household sees nothing rather than everything.
        if (!isSiteAdmin)
        {
            if (householdId is null) return [];
            query = query.Where(f => f.HouseholdId == householdId);
        }

        return await query
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> MarkReadAsync(int id, bool isSiteAdmin, int? householdId, CancellationToken cancellationToken = default)
        => await MutateAsync(id, isSiteAdmin, householdId, f => f.IsRead = true, cancellationToken);

    public async Task<bool> MarkResolvedAsync(int id, bool isSiteAdmin, int? householdId, CancellationToken cancellationToken = default)
        => await MutateAsync(id, isSiteAdmin, householdId, f => { f.IsRead = true; f.IsResolved = true; }, cancellationToken);

    public async Task<bool> ReopenAsync(int id, bool isSiteAdmin, int? householdId, CancellationToken cancellationToken = default)
        => await MutateAsync(id, isSiteAdmin, householdId, f => f.IsResolved = false, cancellationToken);

    /// <summary>
    /// Find the item WITHIN the caller's visibility (R-C1) and apply <paramref name="mutate"/>. Returns false (⇒
    /// the endpoint 404s) when the item doesn't exist or isn't visible to a non-admin — the household scope is in
    /// the WHERE, so a cross-household id is indistinguishable from a missing one (no existence leak).
    /// </summary>
    private async Task<bool> MutateAsync(int id, bool isSiteAdmin, int? householdId, Action<Feedback> mutate, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<Feedback> query = context.Feedbacks.Where(f => f.Id == id);
        if (!isSiteAdmin)
        {
            if (householdId is null) return false;
            query = query.Where(f => f.HouseholdId == householdId);
        }

        var feedback = await query.FirstOrDefaultAsync(cancellationToken);
        if (feedback is null) return false;

        mutate(feedback);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Mutated feedback {FeedbackId} (siteAdmin={IsSiteAdmin})", id, isSiteAdmin);
        return true;
    }
}
