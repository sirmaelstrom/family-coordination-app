namespace FamilyCoordinationApp.Services;

/// <summary>
/// Thrown when a chore mutation fails domain validation or an illegal state transition is attempted
/// (council MN8 — illegal transitions are rejected, never silently coerced). Examples: an empty name,
/// a <c>DayOfMonth</c>-only recurrence (D4-B, monthly-on-day deferred), claiming a chore already held by
/// someone else, dropping a chore the actor does not hold, dropping a deliberately-Assigned chore (drop is
/// Claimed-only), or handing off to a user outside the household.
/// <para>The endpoint layer (WP-06) maps this to <c>400 Bad Request</c>.</para>
/// </summary>
public class ChoreValidationException : Exception
{
    public ChoreValidationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when a referenced chore does not exist for the household.
/// <para>The endpoint layer (WP-06) maps this to <c>404 Not Found</c>.</para>
/// </summary>
public class ChoreNotFoundException : Exception
{
    public ChoreNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when an optimistic-concurrency conflict is detected on the <c>Chore.Version</c> (xmin) token —
/// another writer modified the row after the client read it (council M7/M12, C1). The service sets the
/// client-supplied <c>version</c> as the EF <c>OriginalValue</c> before saving so a stale token surfaces a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>, which is rethrown as this type.
/// <para>The endpoint layer (WP-06) maps this to <c>409 Conflict</c>. The real two-writer race is verified
/// against Postgres in WP-08 — the InMemory provider never raises the underlying concurrency exception.</para>
/// </summary>
public class ChoreConflictException : Exception
{
    public ChoreConflictException(string message) : base(message)
    {
    }

    public ChoreConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
