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
}

/// <summary>
/// An immutable snapshot of <see cref="TenancyOptions"/>, taken when a context is constructed (PR #120 review 2).
/// <see cref="TenancyOptions"/> keeps its setters for configuration binding; a context holds this instead, so no code
/// can flip a kill switch on one context at runtime (D14: the switch is an env change and a restart). Get-only
/// properties, no <c>init</c> accessor (a reflection-visible setter) and no mutable field; a reflection fact in
/// <c>TenantContextTests</c> pins that, and pins that every <see cref="TenancyOptions"/> member is mirrored here.
/// </summary>
internal sealed class TenancySettings
{
    public TenancySettings(TenancyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnforceFilter = options.EnforceFilter;
        EnforceWrites = options.EnforceWrites;
        OutOfRequest = options.OutOfRequest;
    }

    /// <inheritdoc cref="TenancyOptions.EnforceFilter"/>
    public bool EnforceFilter { get; }

    /// <inheritdoc cref="TenancyOptions.EnforceWrites"/>
    public bool EnforceWrites { get; }

    /// <inheritdoc cref="TenancyOptions.OutOfRequest"/>
    public OutOfRequestMode OutOfRequest { get; }
}
