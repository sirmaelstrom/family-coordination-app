using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Helper for handling ID generation race conditions with retry logic.
/// Uses optimistic concurrency: attempt to save, retry with fresh ID on unique violation.
/// </summary>
public static class IdGenerationHelper
{
    private const int MaxRetries = 3;
    private const string PostgresUniqueViolationCode = "23505";

    /// <summary>
    /// Executes an operation that generates an ID, retrying on unique constraint violations.
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
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                lastException = ex;
                logger.LogWarning(
                    "Unique constraint violation on {EntityName} creation (attempt {Attempt}/{MaxRetries}). Retrying with fresh ID.",
                    entityName, attempt, MaxRetries);

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

    /// <summary>
    /// Checks if the exception is a PostgreSQL unique constraint violation.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException pgEx &&
               pgEx.SqlState == PostgresUniqueViolationCode;
    }
}
