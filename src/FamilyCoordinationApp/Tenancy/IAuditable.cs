namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// An entity with a non-nullable <c>CreatedAt</c> (fca-household-scope D16). The write step stamps it on Added entries
/// when it is still <c>default</c>. Independent of <see cref="ITenantEntity"/>. The create-side actor and the modify
/// stamps are <see cref="ICreatedByAudited"/> and <see cref="IUpdateAudited"/> (quest e8bf9e1d).
/// </summary>
public interface IAuditable
{
    DateTime CreatedAt { get; set; }
}

/// <summary>
/// An entity whose Added row records who created it, in a column whose name differs per entity
/// (<c>CreatedByUserId</c>, <c>EnteredByUserId</c>, <c>AddedByUserId</c>). The write step sets that column to the
/// <see cref="TenantState.Caller"/>'s user when the row leaves it unset (<c>null</c> or <c>0</c>), and never touches it
/// on modify. Outside a Caller (inside <c>RunAs</c>, or unfiltered) it stays as the code left it.
/// <para>Implement <see cref="CreatedByProperty"/> explicitly, so EF does not map it.</para>
/// </summary>
public interface ICreatedByAudited
{
    /// <summary>The name of the entity's create-side user-id property.</summary>
    string CreatedByProperty { get; }
}

/// <summary>
/// An entity whose Modified row records when, and optionally by whom, it was last changed. On every Modified entry the
/// write step sets <c>UpdatedAt</c> to the clock's now and, under a <see cref="TenantState.Caller"/>, the
/// <see cref="UpdatedByProperty"/> column to the caller's user, unless the code changed that property itself in the
/// same save (an explicit value wins). Added rows are left as the code built them.
/// <para>Implement <see cref="UpdatedByProperty"/> explicitly, so EF does not map it.</para>
/// </summary>
public interface IUpdateAudited
{
    /// <summary>The name of the entity's <c>UpdatedAt</c> property.</summary>
    const string UpdatedAtProperty = "UpdatedAt";

    /// <summary>The name of the entity's updater user-id property, or <c>null</c> when it has none.</summary>
    string? UpdatedByProperty { get; }
}
