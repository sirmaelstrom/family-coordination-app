using System.Linq.Expressions;

namespace FamilyCoordinationApp.Services.Interfaces;

/// <summary>
/// The one allocator for the per-household composite-key ids (quest 0328001a, operator answer a): <c>RecipeId</c>,
/// <c>CategoryId</c>, <c>ChoreId</c>, <c>RoomId</c>, <c>MealPlanId</c>, <c>EntryId</c>, <c>ShoppingListId</c> and
/// <c>ItemId</c> are counters within their household (or parent row), allocated as <c>max + 1</c> and made safe by
/// retrying the whole insert on a unique violation (Postgres 23505). The model still declares these columns
/// <c>ValueGeneratedOnAdd</c>, but every insert supplies the id, so the identity sequences are unused.
/// <para>Pinned by <c>KeyAllocationArchitectureTests</c>: no other <c>Max</c> over a key column and no other caller of
/// <see cref="IdGenerationHelper"/> in <c>src</c>.</para>
/// </summary>
public interface IKeyAllocator
{
    /// <summary>
    /// Runs <paramref name="insert"/> (passed the 1-based attempt number), re-running it from the start when its save
    /// raises a unique violation a re-run can cure, up to 3 attempts. Only the constraints in
    /// <see cref="IdGenerationHelper.RetryableConstraints"/> are retried (an allocated key's primary key, the meal-plan
    /// week index); a violation on any other unique constraint throws <see cref="ConflictException"/> (409) on the first
    /// attempt, because the row it collides with will still be there (quest d9da6ca4).
    /// <para>The operation must open its own context per attempt so a retry re-reads the max, and must save inside the
    /// delegate: a save after it returns has no retry around it (the
    /// runtime check sees the async scope, not where the save happens). After the last attempt it throws
    /// <see cref="InvalidOperationException"/>. Retry warnings log under the <c>KeyAllocator</c> category, naming
    /// <paramref name="entityName"/>.</para>
    /// </summary>
    Task<TResult> InsertAsync<TResult>(string entityName, Func<int, Task<TResult>> insert);

    /// <summary>
    /// The next key in <paramref name="scope"/>: <c>max(key) + 1</c>, or 1 when the scope is empty. Soft-deleted rows
    /// count (the <c>SoftDelete</c> filter is ignored, so a deleted row's id is never reused); the <c>Tenant</c>
    /// filter stays on, which is what makes the id per-household. Throws <see cref="InvalidOperationException"/>
    /// when called outside <see cref="InsertAsync{TResult}"/>, where no retry would cover a collision.
    /// </summary>
    Task<int> NextKeyAsync<TEntity>(IQueryable<TEntity> scope, Expression<Func<TEntity, int>> key, CancellationToken cancellationToken)
        where TEntity : class;

    /// <summary>
    /// The next key after a snapshot the insert already loaded: <c>max(<paramref name="loadedKeys"/>) + 1</c>, or 1
    /// for an empty snapshot. For a read-and-replace (shopping-list regenerate) this is deliberately the snapshot's max,
    /// not the database's: absent a concurrent hard delete, a rival insert committed since the load holds the
    /// snapshot's next id, so this insert collides and the retry re-runs it from a fresh load. The snapshot must not
    /// be <c>SoftDelete</c>-filtered (a deleted id would be reused and collide on every retry); callers are pinned by
    /// <c>KeyAllocationArchitectureTests</c> rule 3. A database max would hand out fresh ids instead, and a
    /// stale snapshot's replace would then leave both rows (council round 1 on PR #145). Throws like
    /// <see cref="NextKeyAsync{TEntity}"/> outside <see cref="InsertAsync{TResult}"/>.
    /// </summary>
    int NextKeyFromSnapshot(IEnumerable<int> loadedKeys);
}
