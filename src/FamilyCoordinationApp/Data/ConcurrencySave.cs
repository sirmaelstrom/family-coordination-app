using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Data;

/// <summary>
/// The service-level save-with-concurrency path for <see cref="IVersioned"/> entities (quest 8cf3c806): the client's
/// token is applied, and a <see cref="DbUpdateConcurrencyException"/> becomes <see cref="ConcurrencyConflictException"/>,
/// which the /api exception branch answers as 409 with a fixed message. No swallowing and no retry: last-write-wins is
/// the bug.
/// <para>Not every save of a versioned row goes through here (a soft delete, a shopping item delete or reorder save
/// raw). For those, the branch's own <c>DbUpdateConcurrencyException → 409</c> arm in <c>ApiExceptionResponse</c> is
/// the backstop: any raw save's lost update answers the same 409 and fixed message.</para>
/// <para>Every client-token write has one semantic (quest 8cf3c806, operator answer c): a stale or missing token
/// answers 409. There is no merge, no retry and no last-write-wins. Recipe update and the shopping item PATCH were
/// the last two exceptions. <c>ExceptionMappingArchitectureTests</c> pins this file as the only one with a
/// <c>catch (DbUpdateConcurrencyException</c>.</para>
/// </summary>
public static class ConcurrencySave
{
    /// <summary>
    /// Save <paramref name="entity"/>'s pending change checked against the version the caller read.
    /// <para>Loading the row makes EF treat the LOADED <c>Version</c> as the original, so a stale client token would
    /// not conflict on its own. Setting the client token as the <c>OriginalValue</c> makes the statement run
    /// <c>WHERE xmin = &lt;client version&gt;</c>. A missing token is 0 (a non-nullable <c>uint</c> request field
    /// binds it so, and <see cref="MissingToken"/> maps a nullable one), which matches no row (a live row's xmin is
    /// never 0), so it answers 409 (fail-safe).</para>
    /// <para>The entity must be Modified for the version check to run: an unchanged row is not in the UPDATE. Callers
    /// whose request may change nothing touch <c>UpdatedAt</c> so the row is always written.</para>
    /// </summary>
    public static Task SaveWithConcurrencyAsync<TEntity>(
        this ApplicationDbContext context, TEntity entity, uint clientVersion, CancellationToken cancellationToken)
        where TEntity : class, IVersioned
    {
        context.Entry(entity).Property(nameof(IVersioned.Version)).OriginalValue = clientVersion;
        return context.SaveChangesOrConflictAsync(cancellationToken);
    }

    /// <summary>The token a request that omitted it is checked with: 0, which no live row carries (fail-safe 409).</summary>
    public const uint MissingToken = 0;

    /// <summary>
    /// Save, checked against the versions this context loaded. For server-side read-modify-write with no client
    /// token (invite accept/invalidate, calendar-token revoke): a concurrent writer raises
    /// <see cref="ConcurrencyConflictException"/> (409 unless the caller decides otherwise; those three callers map it
    /// to the outcome the user should see, quest f1dc7a24).
    /// </summary>
    public static async Task SaveChangesOrConflictAsync(this ApplicationDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var rows = string.Join(", ", ex.Entries.Select(e => e.Metadata.ClrType.Name));
            throw new ConcurrencyConflictException($"Concurrency conflict saving {rows}; the version read is stale.", ex);
        }
    }
}
