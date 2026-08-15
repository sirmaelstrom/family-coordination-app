using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

/// <summary>
/// The whole feedback surface: <see cref="SubmitAsync"/> (the write side, rebuilt after the WP-12 flip deleted the
/// Blazor dialog that was its only writer) plus the administration reads/mutations lifted out of the direct-EF
/// <c>FeedbackAdmin.razor</c> (review: cluster C). DUAL-MODE
/// visibility (parity): a site admin sees ALL households' feedback; a regular user sees only their own household's.
/// <para><b>R-C1 (IDOR must-fix):</b> the old page did <c>Feedbacks.FindAsync(id)</c> with NO household scoping
/// before flipping flags — safe ONLY because the Blazor list never rendered another household's ids to a
/// non-admin. As a REST surface that becomes an IDOR (a non-admin could POST an arbitrary id). So EVERY read and
/// mutation here is scoped: site admin → any item; non-admin → only an item in their OWN household, else treated
/// as not-found (the endpoint returns 404 with no existence leak). The scope is enforced in the query, not after
/// the fetch.</para>
/// </summary>
public interface IFeedbackService
{
    /// <summary>
    /// Write one feedback item and return its id. The WRITE side of this surface — dead between the WP-12
    /// de-Blazor flip (which deleted <c>FeedbackDialog.razor</c>, the only writer) and its rebuild as
    /// <c>POST /api/settings/feedback</c>.
    /// <para><b>Attribution is server-derived, never client-supplied:</b> <paramref name="userId"/> and
    /// <paramref name="householdId"/> come from the caller's resolved context (M1), so a caller cannot file
    /// feedback into another household. Both stay nullable because the COLUMNS are (they predate this method and
    /// hold rows whose author was deleted), but the submit endpoint always passes a resolved pair — its
    /// unattributed branch is defensive only, and a household-less row would be visible to site admins alone
    /// since a non-admin's list is household-filtered.</para>
    /// <para><paramref name="message"/> is stored trimmed; <paramref name="currentPage"/> and
    /// <paramref name="userAgent"/> are diagnostics truncated to their column limits (500) rather than
    /// rejected — the caller's message must never be lost to a long User-Agent string.</para>
    /// </summary>
    Task<int> SubmitAsync(
        FeedbackType type,
        string message,
        string? currentPage,
        string? userAgent,
        int? userId,
        int? householdId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Feedback for the caller, newest-first. <paramref name="isSiteAdmin"/> ⇒ all households; otherwise only
    /// <paramref name="householdId"/>'s (a non-admin with no resolved household sees nothing). Includes the author
    /// <see cref="Feedback.User"/> for the name mapping.
    /// </summary>
    Task<IReadOnlyList<Feedback>> GetFeedbackAsync(bool isSiteAdmin, int? householdId, CancellationToken cancellationToken = default);

    /// <summary>Mark an item read. Scoped (R-C1): returns false (⇒ 404) when the item doesn't exist OR isn't visible to the caller.</summary>
    Task<bool> MarkReadAsync(int id, bool isSiteAdmin, int? householdId, CancellationToken cancellationToken = default);

    /// <summary>Mark an item resolved (also sets read, parity). Scoped (R-C1): false ⇒ 404 (not found / not visible).</summary>
    Task<bool> MarkResolvedAsync(int id, bool isSiteAdmin, int? householdId, CancellationToken cancellationToken = default);

    /// <summary>Reopen a resolved item. Scoped (R-C1): false ⇒ 404 (not found / not visible).</summary>
    Task<bool> ReopenAsync(int id, bool isSiteAdmin, int? householdId, CancellationToken cancellationToken = default);
}
