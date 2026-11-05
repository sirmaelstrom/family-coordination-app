using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.AgentAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace FamilyCoordinationApp.Authorization;

/// <summary>
/// Names for agent access (quest 8bfa42cf): the bearer scheme, its claims and scopes, and the policies the
/// <c>/mcp</c> endpoint uses. The scheme is never a default: only a policy that names it can authenticate with it,
/// so a bearer token opens nothing on <c>/api</c>, and a browser cookie opens nothing on <c>/mcp</c>.
/// </summary>
public static class AgentAccess
{
    public const string Scheme = "AgentToken";

    /// <summary>Signed in with a valid token that grants <see cref="ReadScope"/>.</summary>
    public const string ReadPolicy = "AgentRead";

    /// <summary>Signed in with a valid token that grants <see cref="WriteScope"/>.</summary>
    public const string WritePolicy = "AgentWrite";

    public const string ScopeClaim = "fca:scope";
    public const string ReadScope = "family.read";
    public const string WriteScope = "family.write";

    public const string TokenIdClaim = "fca:token_id";
    public const string UserIdClaim = "fca:user_id";
    public const string HouseholdIdClaim = "fca:household_id";

    /// <summary>The path prefix the agent endpoint lives under.</summary>
    public static readonly PathString McpPrefix = new("/mcp");

    public static void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(ReadPolicy, policy => policy
            .AddAuthenticationSchemes(Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(ScopeClaim, ReadScope));
        options.AddPolicy(WritePolicy, policy => policy
            .AddAuthenticationSchemes(Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(ScopeClaim, ReadScope)
            .RequireClaim(ScopeClaim, WriteScope));
    }

    /// <summary>The scopes a token's access level grants. Write always includes read.</summary>
    public static IEnumerable<string> ScopesFor(AgentTokenAccess access) => access switch
    {
        AgentTokenAccess.ReadWrite => [ReadScope, WriteScope],
        _ => [ReadScope],
    };

    /// <summary>
    /// The token identity on a principal this scheme authenticated, or <c>null</c>. Checks the identity's
    /// authentication type as well as the claims, so a principal from any other scheme can never be read as one.
    /// </summary>
    public static (int TokenId, int HouseholdId, int UserId)? ReadIdentity(ClaimsPrincipal principal)
    {
        var identity = principal.Identities.FirstOrDefault(i => i.IsAuthenticated && i.AuthenticationType == Scheme);
        if (identity is null) return null;
        return int.TryParse(identity.FindFirst(TokenIdClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tokenId)
               && int.TryParse(identity.FindFirst(HouseholdIdClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var householdId)
               && int.TryParse(identity.FindFirst(UserIdClaim)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? (tokenId, householdId, userId)
            : null;
    }
}

/// <summary>
/// Authenticates <c>Authorization: Bearer fca_pat_…</c> against <see cref="IPersonalAccessTokenService"/>, on every
/// request: a revoked, expired, disabled-member or moved-member token stops working on its next call. No header is
/// <see cref="AuthenticateResult.NoResult"/>; a refused token is a failure whose reason is logged with the token id
/// only (never the secret). Challenge and forbid answer with a JSON <c>{ message }</c> body and never redirect.
/// </summary>
public sealed class AgentTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IPersonalAccessTokenService tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private const string BearerPrefix = "Bearer ";
    private const string RefusedMessage = "The agent token is missing, invalid, expired or revoked.";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers.Authorization;
        if (headers.Count == 0) return AuthenticateResult.NoResult();
        if (headers.Count > 1) return AuthenticateResult.Fail("More than one Authorization header.");

        var header = headers[0];
        if (header is null || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var validation = await tokens.ValidateAsync(header[BearerPrefix.Length..].Trim(), Context.RequestAborted);
        if (validation.Identity is not { } identity)
        {
            Logger.LogInformation("Agent token refused: {Refusal} (token id {TokenId})", validation.Refusal, validation.TokenId);
            return AuthenticateResult.Fail(RefusedMessage);
        }

        var claims = new List<Claim>
        {
            new(AgentAccess.TokenIdClaim, identity.TokenId.ToString(CultureInfo.InvariantCulture)),
            new(AgentAccess.UserIdClaim, identity.UserId.ToString(CultureInfo.InvariantCulture)),
            new(AgentAccess.HouseholdIdClaim, identity.HouseholdId.ToString(CultureInfo.InvariantCulture)),
        };
        claims.AddRange(AgentAccess.ScopesFor(identity.Access).Select(scope => new Claim(AgentAccess.ScopeClaim, scope)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AgentAccess.Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, AgentAccess.Scheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new { message = RefusedMessage }, Context.RequestAborted);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(
            new { message = "This agent token does not grant that. A read-only token cannot make changes." },
            Context.RequestAborted);
    }
}
