using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using FamilyCoordinationApp.Authorization;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.AgentAccess;
using FamilyCoordinationApp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Settings → Agent access (quest 8bfa42cf): the signed-in member lists, mints and revokes their OWN personal access
/// tokens. Cookie-only: this is an <c>/api</c> group behind the /api login gate, whose default scheme is the cookie, so
/// a bearer token cannot reach it; a token can never mint or revoke tokens. Minting also needs a fresh sign-in
/// (<see cref="FreshSignIn"/>). Revoking does not: stopping a leaked token must never wait on a sign-in.
/// <para>The secret appears in exactly one response, the mint's. Nothing returns the stored hash.</para>
/// </summary>
public static class AgentTokenEndpoints
{
    public static IEndpointRouteBuilder MapAgentTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agent-tokens")
            // Login: the /api gate (the FallbackPolicy in Program.cs), not a per-group RequireAuthorization().
            .RequireTenant()
            .DisableAntiforgery();

        group.MapGet("/", List);
        group.MapPost("/", Mint);
        group.MapDelete("/{tokenId:int}", Revoke);
        return app;
    }

    private static async Task<IResult> List(
        CallerScope caller,
        IPersonalAccessTokenService tokens,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var list = await tokens.ListAsync(caller.UserId, ct);
        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var householdName = await context.Households
            .Where(household => household.Id == caller.HouseholdId)
            .Select(household => household.Name)
            .FirstOrDefaultAsync(ct);

        return Results.Ok(new
        {
            householdName,
            expiryPresetDays = PersonalAccessTokenService.ExpiryPresetDays,
            maxLifetimeDays = PersonalAccessTokenService.MaxLifetimeDays,
            tokens = list.Select(ToDto),
        });
    }

    private static async Task<IResult> Mint(
        MintAgentTokenRequest request,
        CallerScope caller,
        ClaimsPrincipal user,
        IHouseholdClock clock,
        IPersonalAccessTokenService tokens,
        CancellationToken ct)
    {
        if (!FreshSignIn.IsFresh(user, clock.UtcNow))
        {
            return Results.Json(
                new { message = "For your security, sign in again before creating an agent token.", code = FreshSignIn.RequiredCode },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var access = request.Access switch
        {
            "read" => AgentTokenAccess.Read,
            "readWrite" => AgentTokenAccess.ReadWrite,
            _ => throw new DomainValidationException("Choose read-only (\"read\") or read and write (\"readWrite\") access."),
        };

        var minted = await tokens.MintAsync(
            caller.HouseholdId,
            caller.UserId,
            new MintAgentTokenCommand(request.Label, access, request.ExpiresInDays, request.ExpiresOn),
            ct);

        return Results.Ok(new
        {
            id = minted.Id,
            token = minted.Token,
            label = minted.Label,
            safePrefix = minted.SafePrefix,
            access = AccessName(minted.Access),
            createdAt = minted.CreatedAt,
            expiresAt = minted.ExpiresAt,
        });
    }

    private static async Task<IResult> Revoke(
        int tokenId,
        CallerScope caller,
        IPersonalAccessTokenService tokens,
        CancellationToken ct)
    {
        await tokens.RevokeAsync(caller.UserId, tokenId, ct);
        return Results.NoContent();
    }

    private static object ToDto(AgentTokenSummary token) => new
    {
        id = token.Id,
        label = token.Label,
        safePrefix = token.SafePrefix,
        access = AccessName(token.Access),
        createdAt = token.CreatedAt,
        expiresAt = token.ExpiresAt,
        revokedAt = token.RevokedAt,
        lastUsedAt = token.LastUsedAt,
        status = token.Status,
    };

    private static string AccessName(AgentTokenAccess access) => access == AgentTokenAccess.ReadWrite ? "readWrite" : "read";
}

/// <summary>Mint body. <c>access</c> is <c>"read"</c> or <c>"readWrite"</c>; exactly one expiry field is set.</summary>
public sealed record MintAgentTokenRequest(
    [Display(Name = "Token name"), RequiredText, MaxTextLength(FieldLengths.PersonalAccessToken.Label)] string Label,
    [Display(Name = "Access"), RequiredText, MaxTextLength(FieldLengths.PersonalAccessToken.Access)] string Access,
    int? ExpiresInDays,
    DateOnly? ExpiresOn);
