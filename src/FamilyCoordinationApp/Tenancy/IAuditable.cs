namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// An entity with a non-nullable <c>CreatedAt</c> (fca-household-scope D16). WP-03's write step stamps it on
/// Added entries when it is still <c>default</c>. <c>CreatedAt</c> only: <c>UpdatedAt</c> is nullable on most
/// entities and goes to the audit follow-up quest. Independent of <see cref="ITenantEntity"/>.
/// </summary>
public interface IAuditable
{
    DateTime CreatedAt { get; set; }
}
