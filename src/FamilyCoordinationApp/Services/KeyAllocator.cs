using System.Linq.Expressions;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <inheritdoc cref="IKeyAllocator"/>
public sealed class KeyAllocator(ILogger<KeyAllocator> logger) : IKeyAllocator
{
    // True while an InsertAsync operation is running on this async flow. AsyncLocal flows into the operation's
    // awaits and is restored when InsertAsync returns, so two concurrent inserts each see their own value.
    private static readonly AsyncLocal<bool> InsideInsert = new();

    public async Task<TResult> InsertAsync<TResult>(string entityName, Func<int, Task<TResult>> insert)
    {
        var outer = InsideInsert.Value;
        InsideInsert.Value = true;
        try
        {
            return await IdGenerationHelper.ExecuteWithRetryAsync(insert, logger, entityName);
        }
        finally
        {
            InsideInsert.Value = outer;
        }
    }

    public async Task<int> NextKeyAsync<TEntity>(
        IQueryable<TEntity> scope, Expression<Func<TEntity, int>> key, CancellationToken cancellationToken)
        where TEntity : class
    {
        EnsureInsideInsert(typeof(TEntity).Name);

        var max = await scope
            .IgnoreQueryFilters(["SoftDelete"])
            .Select(key)
            .Select(id => (int?)id)
            .MaxAsync(cancellationToken);

        return (max ?? 0) + 1;
    }

    public int NextKeyFromSnapshot(IEnumerable<int> loadedKeys)
    {
        EnsureInsideInsert("snapshot");
        return loadedKeys.DefaultIfEmpty(0).Max() + 1;
    }

    private static void EnsureInsideInsert(string what)
    {
        if (!InsideInsert.Value)
        {
            throw new InvalidOperationException(
                $"A {what} key was allocated outside IKeyAllocator.InsertAsync; nothing would retry a collision.");
        }
    }
}
