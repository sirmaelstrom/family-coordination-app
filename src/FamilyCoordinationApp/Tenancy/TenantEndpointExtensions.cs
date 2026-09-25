namespace FamilyCoordinationApp.Tenancy;

/// <summary>
/// Endpoint metadata marking a route as tenant-scoped: <see cref="CallerTenantMiddleware"/> resolves the caller
/// and sets the <see cref="ITenantContext"/> before the handler runs (fca-household-scope D3).
/// </summary>
public sealed class TenantScopedMetadata
{
    public static readonly TenantScopedMetadata Instance = new();

    private TenantScopedMetadata() { }
}

/// <summary>
/// <c>RequireTenant()</c> for route groups and single endpoints (D3). The groups are not nested, so the marker is
/// applied per group. The marker map (what is marked, and the pinned unmarked list) is enforced by the
/// endpoint-coverage fact in <c>TenantPlumbingTests</c>.
/// </summary>
public static class TenantEndpointExtensions
{
    public static RouteGroupBuilder RequireTenant(this RouteGroupBuilder builder) =>
        builder.WithMetadata(TenantScopedMetadata.Instance);

    public static RouteHandlerBuilder RequireTenant(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(TenantScopedMetadata.Instance);
}
