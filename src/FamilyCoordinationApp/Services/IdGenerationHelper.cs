using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Helper for handling ID generation race conditions with retry logic.
/// Uses optimistic concurrency: attempt to save, retry with fresh ID on a unique violation a retry can cure.
/// </summary>
public static class IdGenerationHelper
{
    private const int MaxRetries = 3;

    /// <summary>
    /// The unique constraints whose violation a re-run of the insert cures (quest d9da6ca4): the primary key of every
    /// table whose id <c>IKeyAllocator</c> allocates as <c>max + 1</c> (a rival took that id; the re-run reads the new
    /// max), plus the meal-plan week index (a rival created the week's plan; the re-run finds it, quest f5ca4b08). A
    /// violation on any other constraint names a row that already exists, so it is not retried: it throws
    /// <see cref="ConflictException"/> (409) on the first attempt. Each name is checked against the migrated schema by
    /// <c>UniqueConflictTests</c>. A new allocated key's table adds its primary key here.
    /// </summary>
    public static readonly FrozenSet<string> RetryableConstraints = new[]
    {
        "PK_Categories",
        "PK_Chores",
        "PK_MealPlans",
        "PK_MealPlanEntries",
        "PK_Recipes",
        "PK_Rooms",
        "PK_ShoppingLists",
        "PK_ShoppingListItems",
        "IX_MealPlans_HouseholdId_WeekStartDate",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Executes an operation that generates an ID, retrying on a unique violation of a
    /// <see cref="RetryableConstraints">retryable constraint</see>. Any other unique violation throws
    /// <see cref="ConflictException"/> at once.
    /// </summary>
    /// <typeparam name="TResult">The return type of the operation</typeparam>
    /// <param name="operation">The operation to execute. Returns the result on success.</param>
    /// <param name="logger">Logger for tracking retry attempts</param>
    /// <param name="entityName">Name of the entity for logging</param>
    /// <returns>The result of the operation</returns>
    public static async Task<TResult> ExecuteWithRetryAsync<TResult>(
        Func<int, Task<TResult>> operation,
        ILogger logger,
        string entityName)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await operation(attempt);
            }
            catch (DbUpdateException ex) when (UniqueViolation.Of(ex) is { } violation)
            {
                if (!IsRetryable(violation.ConstraintName))
                {
                    throw UniqueViolation.ToConflict(ex, violation, entityName);
                }

                lastException = ex;
                logger.LogWarning(
                    "Unique constraint {Constraint} violated on {EntityName} creation (attempt {Attempt}/{MaxRetries}). Retrying with fresh ID.",
                    violation.ConstraintName, entityName, attempt, MaxRetries);

                // Brief delay before retry to reduce collision probability
                if (attempt < MaxRetries)
                {
                    await Task.Delay(10 * attempt);
                }
            }
        }

        logger.LogError(lastException,
            "Failed to create {EntityName} after {MaxRetries} attempts due to ID collisions",
            entityName, MaxRetries);

        throw new InvalidOperationException(
            $"Failed to generate unique ID for {entityName} after {MaxRetries} attempts. " +
            "This may indicate high concurrent write activity.",
            lastException);
    }

    /// <summary>A missing constraint name is not retryable: nothing says a re-run would cure it.</summary>
    private static bool IsRetryable(string? constraintName) =>
        constraintName is not null && RetryableConstraints.Contains(constraintName);
}
