using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// The feedback surface, both halves. Reads/mutations are the strangler lift of <c>FeedbackAdmin.razor</c>'s
/// direct-EF logic into a testable, dual-mode service; <see cref="SubmitAsync"/> is the write side, rebuilt after
/// the WP-12 flip deleted <c>FeedbackDialog.razor</c> — the app's only writer — and left the admin inbox with no
/// way to receive anything. Short-lived contexts via the factory. Every read and mutation is household-scoped for
/// a non-admin (R-C1, the IDOR fix): the scope is part of the query, so a non-admin posting another household's id
/// finds nothing → the endpoint 404s with no existence leak. A site admin is unscoped (sees/acts on any item).
/// </summary>
public sealed class FeedbackService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<FeedbackService> logger) : IFeedbackService
{
    /// <summary>Column limit for <c>CurrentPage</c> and <c>UserAgent</c> (<c>FeedbackConfiguration</c>).</summary>
    private const int DiagnosticMaxLength = 500;

    /// <summary>Column limit for <c>Message</c> (<c>FeedbackConfiguration</c>); the endpoint 400s past it.</summary>
    public const int MessageMaxLength = 4000;

    public async Task<int> SubmitAsync(
        FeedbackType type,
        string message,
        string? currentPage,
        string? userAgent,
        int? userId,
        int? householdId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var feedback = new Feedback
        {
            // Attribution is the CALLER's resolved context, never a request field (M1) — the submit body carries
            // no ids, so there is nothing for a caller to point at another household.
            UserId = userId,
            HouseholdId = householdId,
            Type = type,
            Message = message.Trim(),
            // Diagnostics: truncate rather than reject. A 501-char User-Agent must not cost the user their
            // message, and neither value is content the user authored.
            CurrentPage = Truncate(currentPage),
            UserAgent = Truncate(userAgent),
            CreatedAt = DateTime.UtcNow,
        };

        context.Feedbacks.Add(feedback);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Feedback {FeedbackId} submitted (type={Type}, household={HouseholdId}, user={UserId})",
            feedback.Id, type, householdId, userId);

        return feedback.Id;
    }

    /// <summary>Trim + cap a diagnostic field to its column limit; whitespace-only becomes null.</summary>
    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= DiagnosticMaxLength ? trimmed : trimmed[..DiagnosticMaxLength];
    }

    public async Task<IReadOnlyList<Feedback>> GetFeedbackAsync(bool isSiteAdmin, int? householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Read-only projection ⇒ AsNoTracking (council R6).
        IQueryable<Feedback> query = context.Feedbacks.AsNoTracking().Include(f => f.User);

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
