namespace FamilyCoordinationApp.Services;

/// <summary>
/// Thrown when an optimistic-concurrency conflict is detected on the <c>MealPlanEntry.Version</c> (xmin) token —
/// another writer modified the entry after the client read it. Mirrors <see cref="ChoreConflictException"/> and
/// <see cref="RecipeConflictException"/>: the service sets the client-supplied version as the EF
/// <c>OriginalValue</c> before saving, so a stale token surfaces a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>, rethrown as this type.
/// <para>The endpoint layer maps this to <c>409 Conflict</c> (with a non-empty body). The real stale-token
/// behavior is verified against Postgres in the integration tests — the InMemory provider has no xmin.</para>
/// </summary>
public class MealPlanConflictException : Exception
{
    public MealPlanConflictException(string message) : base(message)
    {
    }

    public MealPlanConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
