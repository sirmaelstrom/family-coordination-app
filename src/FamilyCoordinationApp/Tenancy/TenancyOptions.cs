namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// The <c>Tenancy</c> configuration section (fca-household-scope D14/D15). Every default is the ENFORCING value
/// (constraints M7): no committed app config, compose file or appsettings sets any of these to a non-default.
/// The only non-default in the repo is <c>OutOfRequest=Unfiltered</c>, set in test hosts by
/// <c>TestHostTenancy.Apply</c>.
/// </summary>
public sealed class TenancyOptions
{
    /// <summary>The read filter's incident-rollback kill switch (D14). Read by WP-02's filter.</summary>
    public bool EnforceFilter { get; set; } = true;

    /// <summary>The write step's kill switch (D14). Read by WP-03's <c>SaveChanges</c> step.</summary>
    public bool EnforceWrites { get; set; } = true;

    /// <summary>What an Unset tenant means outside any HTTP request (D15).</summary>
    public OutOfRequestMode OutOfRequest { get; set; } = OutOfRequestMode.Throw;

    /// <summary>
    /// A copy for one context, so no context aliases the shared <c>IOptions</c> value. Every member is a value type,
    /// so a memberwise copy is complete.
    /// </summary>
    internal TenancyOptions Snapshot() => (TenancyOptions)MemberwiseClone();
}
