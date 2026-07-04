using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Services;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// /api/presence/* — exposes the in-memory <see cref="PresenceService"/> over HTTP so the de-Blazored
/// SvelteKit shell can drive the header's online-users avatar row + a client-derived sync indicator,
/// replacing the Blazor circuit heartbeat (D3). <c>POST /heartbeat</c> records the caller's activity + the
/// SPA path they're on; <c>GET /users</c> returns the caller-excluded active users as a typed DTO.
/// <para>
/// CRITICAL: <c>GET /users</c> FIRST runs <see cref="PresenceService.UpdatePresence"/> — the
/// Online→Away→Offline staleness decay that <c>PollingService</c> used to drive on a timer. WP-12 deletes
/// PollingService, so without this call a user who closed their tab would linger <c>Online</c> forever.
/// The endpoints only ADD to the backend (M1); the Blazor MainLayout heartbeat + PollingService are
/// removed in WP-12 after the M5 consumer audit.
/// </para>
/// </summary>
public static class PresenceEndpoints
{
    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder app)
    {
        // Same group posture as the other island endpoint groups: cookie auth + no antiforgery token
        // (the SPA calls these with credentials: 'include' and no CSRF token — same-origin cookie only, M2).
        var group = app.MapGroup("/api/presence")
            .RequireAuthorization()
            .DisableAntiforgery();

        group.MapPost("/heartbeat", Heartbeat);
        group.MapGet("/users", GetUsers);

        return app;
    }

    private static async Task<IResult> Heartbeat(
        HeartbeatRequest request,
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        PresenceService presence,
        CancellationToken ct)
    {
        // PresenceService.Heartbeat needs display name / initials / picture, which UserContextResolver
        // does not carry — so load the same projection MeEndpoints does (identity from the DB, never the
        // client). The caller's user id + household come ONLY from their email claim (multi-tenant boundary).
        var email = principal.FindFirst(ClaimTypes.Email)?.Value;
        if (string.IsNullOrEmpty(email)) return Results.Unauthorized();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users
            .Where(u => u.Email == email)
            .Select(u => new { u.Id, u.HouseholdId, u.DisplayName, u.Initials, u.PictureUrl })
            .FirstOrDefaultAsync(ct);

        if (user is null) return Results.Unauthorized();

        presence.Heartbeat(user.Id, user.HouseholdId, user.DisplayName, user.PictureUrl, user.Initials, request.Page);
        return Results.NoContent();
    }

    private static async Task<IResult> GetUsers(
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        PresenceService presence,
        CancellationToken ct)
    {
        var caller = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (caller is null) return Results.Unauthorized();

        // Drive the staleness decay PollingService used to run on a timer (WP-12 deletes it), so closed-tab
        // users age Online→Away→Offline instead of showing Online forever. Idempotent + cheap (in-memory).
        presence.UpdatePresence();

        // Household-scoped (multi-tenant boundary), caller-excluded, and projected to a typed DTO —
        // never the raw UserPresence, which would leak LastSeen / CurrentPage. Status is the PresenceStatus
        // enum, serialized "online"/"away" by the global camelCase JsonStringEnumConverter (Program.cs).
        var users = presence.GetAllActiveUsers(caller.HouseholdId)
            .Where(p => p.UserId != caller.UserId)
            .Select(p => new PresenceUserDto(p.UserId, p.DisplayName, p.Initials, p.PictureUrl, p.Status))
            .ToList();

        return Results.Ok(users);
    }

    /// <summary>POST body for a heartbeat: the SPA path the caller is currently on (nullable/cosmetic).</summary>
    public sealed record HeartbeatRequest(string? Page);

    /// <summary>The caller-excluded active-user shape the shell header renders (no LastSeen / CurrentPage).</summary>
    public sealed record PresenceUserDto(
        int UserId,
        string DisplayName,
        string? Initials,
        string? PictureUrl,
        PresenceStatus Status);
}
