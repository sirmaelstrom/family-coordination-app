namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// An entity with a Postgres <c>xmin</c> row version (<c>IsRowVersion()</c> on <see cref="Version"/>), saved through
/// <see cref="ConcurrencySave"/> so a lost update surfaces as one <c>ConcurrencyConflictException</c> (409) (quest
/// 8cf3c806). <c>ExceptionMappingArchitectureTests</c> holds the set of implementers equal to the model's row-versioned
/// entity types.
/// </summary>
public interface IVersioned
{
    uint Version { get; set; }
}
