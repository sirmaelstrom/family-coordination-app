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
    /// raises a unique violation, up to 3 attempts. The operation must open its own context per attempt so a retry
    /// re-reads the max. After the last attempt it throws <see cref="InvalidOperationException"/>.
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
}
