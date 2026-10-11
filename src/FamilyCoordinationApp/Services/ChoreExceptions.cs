namespace FamilyCoordinationApp.Services;

/// <summary>
/// Thrown when a chore mutation fails domain validation or an illegal state transition is attempted
/// (council MN8 — illegal transitions are rejected, never silently coerced). Examples: an empty name,
/// a <c>DayOfMonth</c>-only recurrence (D4-B, monthly-on-day deferred), claiming a chore already held by
/// someone else, dropping a chore the actor does not hold, dropping a deliberately-Assigned chore (drop is
/// Claimed-only), or handing off to a user outside the household.
/// <para>The /api exception branch maps it to <c>400 Bad Request</c>; the message is client-facing.</para>
/// </summary>
public class ChoreValidationException : DomainValidationException
{
    public ChoreValidationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when a referenced chore does not exist for the household.
/// <para>The /api exception branch maps it to <c>404 Not Found</c> with a fixed message; the constructor's
/// message (which names ids) is log detail only.</para>
/// </summary>
public class ChoreNotFoundException : NotFoundException
{
    public ChoreNotFoundException(string logDetail) : base("Chore not found.", logDetail)
    {
    }
}

// The optimistic-concurrency conflict on Chore.Version is ConcurrencyConflictException, raised by
// Data.ConcurrencySave (quest 8cf3c806); the per-entity ChoreConflictException is gone.
