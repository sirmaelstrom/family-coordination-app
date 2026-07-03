using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Services;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// GET /api/me — the SPA's session bootstrap. Returns the authenticated caller's household + user
/// identity (resolved server-side from the cookie claims + DB), mirroring what each Razor island host
/// resolved in OnInitializedAsync before handing it to the island as data-* attributes. The de-Blazored
/// SvelteKit shell calls this once on load to build the island ShellContext and to drive the header
/// (display name / initials / avatar / site-admin chip). HouseholdId/UserId come ONLY from here — never
/// from client input (the multi-tenant boundary, same invariant as UserContextResolver).
/// </summary>
public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", GetMe)
            .RequireAuthorization()
            .DisableAntiforgery();

        return app;
    }

    private static async Task<IResult> GetMe(
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ISiteAdminService siteAdmin,
        CancellationToken ct)
    {
        var email = principal.FindFirst(ClaimTypes.Email)?.Value;
        if (string.IsNullOrEmpty(email)) return Results.Unauthorized();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users
            .Where(u => u.Email == email)
            .Select(u => new
            {
                u.Id,
                u.HouseholdId,
                u.DisplayName,
                u.Initials,
                u.PictureUrl,
            })
            .FirstOrDefaultAsync(ct);

        if (user is null) return Results.Unauthorized();

        var claimName = principal.FindFirst(ClaimTypes.Name)?.Value;

        return Results.Ok(new MeDto(
            user.HouseholdId,
            user.Id,
            // Prefer the stored display name; fall back to the OAuth name claim, then email.
            string.IsNullOrWhiteSpace(user.DisplayName)
                ? (string.IsNullOrWhiteSpace(claimName) ? email : claimName)
                : user.DisplayName,
            user.Initials,
            user.PictureUrl,
            siteAdmin.IsSiteAdmin(email)));
    }

    public sealed record MeDto(
        int HouseholdId,
        int UserId,
        string UserName,
        string? Initials,
        string? PictureUrl,
        bool IsSiteAdmin);
}
