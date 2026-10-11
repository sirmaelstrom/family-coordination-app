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
/// <para>The one deliberate exception is <c>ShoppingListService.UpdateItemWithConcurrencyAsync</c>, which merges
/// ("checked wins") and retries instead, because the shopping item PATCH carries no version.
/// <c>ExceptionMappingArchitectureTests</c> pins those two as the only files with a <c>catch (DbUpdateConcurrencyException</c>.</para>
/// </summary>
public static class ConcurrencySave
{
    /// <summary>
    /// Save <paramref name="entity"/>'s pending change checked against the version the caller read.
    /// <para>Loading the row makes EF treat the LOADED <c>Version</c> as the original, so a stale client token would
    /// not conflict on its own. Setting the client token as the <c>OriginalValue</c> makes the statement run
    /// <c>WHERE xmin = &lt;client version&gt;</c>. A request record whose token is a non-nullable <c>uint</c> binds a
    /// missing token to 0, which matches no row, so it answers 409 (fail-safe).</para>
    /// <para><paramref name="clientVersion"/> <c>null</c> means the caller supplied no token: the save is checked
    /// against the version this context loaded, so only a write landing between load and save conflicts. Only
    /// <c>RecipeService.UpdateRecipeAsync</c> passes a nullable token (its legacy last-write-wins semantic).</para>
    /// </summary>
    public static Task SaveWithConcurrencyAsync<TEntity>(
        this ApplicationDbContext context, TEntity entity, uint? clientVersion, CancellationToken cancellationToken)
        where TEntity : class, IVersioned
    {
        if (clientVersion is { } version)
        {
            context.Entry(entity).Property(nameof(IVersioned.Version)).OriginalValue = version;
        }

        return context.SaveChangesOrConflictAsync(cancellationToken);
    }

    /// <summary>
    /// Save, checked against the versions this context loaded. For server-side read-modify-write with no client
    /// token (invite accept/invalidate, calendar-token revoke): a concurrent writer answers 409, not 500.
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
