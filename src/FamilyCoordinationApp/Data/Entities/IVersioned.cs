namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// An entity with a Postgres <c>xmin</c> row version (<c>IsRowVersion()</c> on <see cref="Version"/>) (quest 8cf3c806).
/// A client-token mutation saves through <see cref="ConcurrencySave"/>, so a lost update surfaces as a
/// <c>ConcurrencyConflictException</c> (409). Any raw save of one of these rows is backstopped by
/// <c>ApiExceptionResponse</c>'s <c>DbUpdateConcurrencyException → 409</c> arm, with the same fixed message.
/// <c>ExceptionMappingArchitectureTests</c> holds the set of implementers equal to the model's row-versioned
/// entity types.
/// </summary>
public interface IVersioned
{
    uint Version { get; set; }
}
