namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// The <c>Tenancy</c> configuration section (fca-household-scope D15). The default is the ENFORCING value
/// (constraints M7): no committed app config, compose file or appsettings sets it to a non-default. The only
/// non-default in the repo is <c>OutOfRequest=Unfiltered</c>, set in test hosts by <c>TestHostTenancy.Apply</c>.
/// WP-05 removed the D14 rollout kill switches (<c>EnforceFilter</c>, <c>EnforceWrites</c>); a leftover
/// <c>Tenancy__EnforceFilter</c> or <c>Tenancy__EnforceWrites</c> in an environment binds to nothing.
/// </summary>
public sealed class TenancyOptions
{
    /// <summary>What an Unset tenant means outside any HTTP request (D15).</summary>
    public OutOfRequestMode OutOfRequest { get; set; } = OutOfRequestMode.Throw;
}

/// <summary>
/// An immutable snapshot of <see cref="TenancyOptions"/>, taken when a context is constructed (PR #120 review 2).
/// <see cref="TenancyOptions"/> keeps its setters for configuration binding; a context holds this instead, so no code
/// can change a setting on one context at runtime. Get-only properties, no <c>init</c> accessor (a reflection-visible
/// setter) and no mutable field; a reflection fact in <c>TenantContextTests</c> pins that, and pins that every
/// <see cref="TenancyOptions"/> member is mirrored here.
/// </summary>
internal sealed class TenancySettings
{
    public TenancySettings(TenancyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        OutOfRequest = options.OutOfRequest;
    }

    /// <inheritdoc cref="TenancyOptions.OutOfRequest"/>
    public OutOfRequestMode OutOfRequest { get; }
}
