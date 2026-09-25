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
/// <c>RequireTenant()</c> for route groups and single endpoints (D3). Groups ARE nested (<c>SettingsEndpoints</c>
/// maps <c>/categories</c> and <c>/members</c> inside the marked <c>/api/settings</c> group), and the marker carries
/// into nested groups, so it is applied once per top-level group. The marker map (what is marked, and the pinned unmarked list) is enforced by the
/// endpoint-coverage fact in <c>TenantPlumbingTests</c>.
/// </summary>
public static class TenantEndpointExtensions
{
    public static RouteGroupBuilder RequireTenant(this RouteGroupBuilder builder) =>
        builder.WithMetadata(TenantScopedMetadata.Instance);

    public static RouteHandlerBuilder RequireTenant(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(TenantScopedMetadata.Instance);
}
