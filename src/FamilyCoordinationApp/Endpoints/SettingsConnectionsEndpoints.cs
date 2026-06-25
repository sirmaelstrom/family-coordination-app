using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Minimal-API surface for Settings island B (Connections, strangler — mirrors <see cref="SettingsEndpoints"/>):
/// an <c>/api/settings/connections</c> group behind <c>.RequireAuthorization().DisableAntiforgery()</c>, every
/// handler resolving the HouseholdId/UserId from the authenticated caller (M1, never client-supplied) via
/// <see cref="UserContextResolver"/>. Thin over the existing <see cref="IHouseholdConnectionService"/> — the real
/// work is the island's client-side invite state machine, not this surface.
///
/// <para><b>200-with-outcome (review §8):</b> validate / accept return <c>200</c> with an outcome envelope
/// (<c>{ isValid, … }</c> / <c>{ success, … }</c>), NOT a 4xx — an invalid / expired / self / already-connected
/// code is an expected user-flow result, rendered inline as a warning today. This keeps the island's api.ts on
/// the happy path and the error-mapping client-side (parity <c>MapValidationError</c>).</para>
///
/// <para><b>Non-empty 4xx (review R-B4):</b> the only genuine 4xx here is <c>401</c> (unresolved caller). The
/// app-global <c>UseStatusCodePagesWithReExecute</c> turns an empty-body non-GET 4xx into a 405, so any 4xx we
/// add later must carry a body — see <see cref="SettingsEndpoints"/>.</para>
///
/// <para><b>Disconnect M1 + idempotency (review R-B2):</b> <c>DELETE /connected/{householdId}</c> →
/// <c>DisconnectHouseholdsAsync(callerHh, householdId)</c>. M1 holds because one arg is ALWAYS the server-resolved
/// caller household — a caller can only affect a pairing involving their own household (passing a stranger's id
/// just no-ops). The service no-ops to nothing when the pairing is already gone, so the endpoint stays a clean
/// idempotent <c>204</c> (double-click safe).</para>
/// </summary>
public static class SettingsConnectionsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsConnectionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings/connections")
            .RequireAuthorization()
            .DisableAntiforgery();

        group.MapGet("/", GetConnections);
        group.MapPost("/invite", GenerateInvite);
        group.MapDelete("/invite", CancelInvite);
        group.MapPost("/validate", ValidateCode);
        group.MapPost("/accept", AcceptCode);
        group.MapDelete("/connected/{householdId:int}", Disconnect);

        return app;
    }

    /// <summary>#1 GET / — the active invite (null when none) + connected households, in one payload (parity LoadData).</summary>
    private static async Task<IResult> GetConnections(
        ClaimsPrincipal principal,
        IHouseholdConnectionService connectionService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var invite = await connectionService.GetActiveInviteAsync(user.HouseholdId, ct);
        var connected = await connectionService.GetConnectedHouseholdsAsync(user.HouseholdId, ct);

        return Results.Ok(new ConnectionsDto(
            invite is null ? null : new ConnectionInviteDto(invite.InviteCode, invite.ExpiresAt),
            connected.Select(c => new ConnectedFamilyDto(c.HouseholdId, c.HouseholdName, c.ConnectedAt)).ToList()));
    }

    /// <summary>#2 POST /invite — generate (replaces any existing active invite) → the new code (201).</summary>
    private static async Task<IResult> GenerateInvite(
        ClaimsPrincipal principal,
        IHouseholdConnectionService connectionService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var invite = await connectionService.GenerateInviteAsync(user.HouseholdId, user.UserId, cancellationToken: ct);
        return Results.Created(
            "/api/settings/connections",
            new ConnectionInviteDto(invite.InviteCode, invite.ExpiresAt));
    }

    /// <summary>#3 DELETE /invite — cancel the household's active invite → 204 (idempotent; no active invite is a no-op).</summary>
    private static async Task<IResult> CancelInvite(
        ClaimsPrincipal principal,
        IHouseholdConnectionService connectionService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        await connectionService.InvalidateInviteAsync(user.HouseholdId, ct);
        return Results.NoContent();
    }

    /// <summary>#4 POST /validate — check a code WITHOUT connecting → 200 outcome envelope (parity: warning, never an HTTP error).</summary>
    private static async Task<IResult> ValidateCode(
        ValidateRequest req,
        ClaimsPrincipal principal,
        IHouseholdConnectionService connectionService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        // An empty/whitespace code is treated as invalid (the island guards length===6, but stay on the 200
        // envelope and never let a null code reach the service's .Trim()).
        if (string.IsNullOrWhiteSpace(req.Code))
        {
            return Results.Ok(new ValidateInviteResultDto(false, null, "Invalid invite code."));
        }

        var (isValid, householdName, error) = await connectionService.ValidateInviteCodeAsync(req.Code, user.HouseholdId, ct);
        return Results.Ok(new ValidateInviteResultDto(isValid, householdName, error));
    }

    /// <summary>#5 POST /accept — establish the connection → 200 outcome envelope (failure returns to the entry view, review R-B3).</summary>
    private static async Task<IResult> AcceptCode(
        AcceptRequest req,
        ClaimsPrincipal principal,
        IHouseholdConnectionService connectionService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Code))
        {
            return Results.Ok(new AcceptInviteResultDto(false, null, "Invalid invite code."));
        }

        var (success, connectedName, error) = await connectionService.AcceptInviteAsync(req.Code, user.HouseholdId, user.UserId, ct);
        return Results.Ok(new AcceptInviteResultDto(success, connectedName, error));
    }

    /// <summary>#6 DELETE /connected/{householdId} — stop sharing with a connected household → 204 (M1 + idempotent, review R-B2).</summary>
    private static async Task<IResult> Disconnect(
        int householdId,
        ClaimsPrincipal principal,
        IHouseholdConnectionService connectionService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        // M1: one arg is always the resolved caller household, so this can only drop a pairing involving the
        // caller. A stranger id (or an already-gone pairing) no-ops → still 204.
        await connectionService.DisconnectHouseholdsAsync(user.HouseholdId, householdId, ct);
        return Results.NoContent();
    }
}

// ─── Request DTOs ───────────────────────────────────────────────────────────────

public sealed record ValidateRequest(string Code);
public sealed record AcceptRequest(string Code);
