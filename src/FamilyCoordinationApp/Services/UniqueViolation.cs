using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// A PostgreSQL unique violation (SQLSTATE 23505) read off an EF save failure, and the 409 it answers when re-running
/// the save cannot cure it (quest d9da6ca4). The constraint name is what tells a cure from a conflict:
/// <see cref="IdGenerationHelper.RetryableConstraints"/> lists the ones a retry cures; any other names a row that
/// already exists, and the caller gets <see cref="ConflictException"/> rather than a 500.
/// </summary>
public static class UniqueViolation
{
    /// <summary>The filtered unique index on an active category's name within a household (CategoryConfiguration).</summary>
    public const string CategoryNameIndex = "IX_Categories_HouseholdId_Name";

    public const string CategoryNameMessage = "A category with that name already exists.";

    public const string GenericMessage = "This conflicts with something that already exists. Reload and try again.";

    /// <summary>The Postgres error when <paramref name="ex"/> is a unique violation, else null.</summary>
    public static PostgresException? Of(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg ? pg : null;

    /// <summary>The 409 for a unique violation on <paramref name="violation"/>'s constraint, saving <paramref name="what"/>.</summary>
    public static ConflictException ToConflict(DbUpdateException ex, PostgresException violation, string what) =>
        new(MessageFor(violation.ConstraintName),
            $"Unique violation on {violation.ConstraintName ?? "(unnamed constraint)"} saving {what}",
            ex);

    private static string MessageFor(string? constraintName) => constraintName switch
    {
        CategoryNameIndex => CategoryNameMessage,
        _ => GenericMessage,
    };
}
