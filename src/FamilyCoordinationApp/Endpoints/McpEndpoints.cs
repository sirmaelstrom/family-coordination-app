using FamilyCoordinationApp.Authorization;
using FamilyCoordinationApp.Services.AgentAccess;
using FamilyCoordinationApp.Tenancy;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// The agent endpoint at <c>/mcp</c> (quest 8bfa42cf). Mapped only when <c>Mcp:Enabled</c> is true; off, the path
/// is an ordinary 404 exactly as before. Authorization is explicit and bearer-only: the group names
/// <see cref="AgentAccess.ReadPolicy"/>, whose only scheme is the agent token, so the cookie (and the Development
/// auth bypass) cannot authenticate here, and the /api fallback gate, which succeeds off <c>/api</c>, never applies.
/// <para>This change ships the credential and the gate only; the protocol and its tools arrive with the MCP SDK in a
/// later change, so a valid token currently gets 501.</para>
/// </summary>
public static class McpEndpoints
{
    public const string EnabledSetting = "Mcp:Enabled";

    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>(EnabledSetting)) return app;

        var group = app.MapGroup(AgentAccess.McpPrefix)
            .RequireAuthorization(AgentAccess.ReadPolicy)
            .RequireTenant()
            .DisableAntiforgery()
            .AddEndpointFilter(RecordTokenUse);

        group.MapPost("", () => Results.Json(
            new { message = "The agent endpoint is not available yet." },
            statusCode: StatusCodes.Status501NotImplemented));
        return app;
    }

    /// <summary>Stamp the token's last use (throttled in the service) once the caller tenant is set.</summary>
    private static async ValueTask<object?> RecordTokenUse(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (AgentAccess.ReadIdentity(http.User) is { } agent)
        {
            await http.RequestServices.GetRequiredService<IPersonalAccessTokenService>().TouchAsync(agent.TokenId, http.RequestAborted);
        }
        return await next(context);
    }
}
