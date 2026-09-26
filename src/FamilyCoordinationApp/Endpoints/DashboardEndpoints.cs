using System.Security.Claims;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tenancy;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Minimal-API surface for the dashboard island (strangler — mirrors <see cref="MealPlanEndpoints"/>): a
/// <c>/api/dashboard</c> group behind <c>.RequireAuthorization().DisableAntiforgery()</c>. ONE read route —
/// the dashboard is a pure read-aggregate (D1). The handler takes the HouseholdId/UserId of the
/// authenticated caller (M1, never client-supplied) as a <see cref="CallerScope"/>, resolves the
/// greeting name from the caller's claims, and delegates the whole <c>DashboardDto</c> assembly to
/// <see cref="IDashboardService"/> (ONE projection — no drift, M9).
///
/// <para>Read-only: no writes ⇒ no concurrency token, no 4xx-on-missing path (an empty household yields a
/// well-formed 200 with zero counts / empty meals), so the /api generic-error-body backfill does not apply.</para>
/// </summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard")
            .RequireAuthorization()
            .RequireTenant()
            // Kept consistent with the other island endpoint groups (the island calls this with same-origin
            // credentialed fetch; there are no writes here, so antiforgery is moot either way).
            .DisableAntiforgery();

        group.MapGet("/", GetDashboard);

        return app;
    }

    private static async Task<IResult> GetDashboard(
        CallerScope caller,
        ClaimsPrincipal principal,
        IDashboardService dashboardService,
        CancellationToken ct)
    {
        var greetingName = ResolveGreetingName(principal);
        var dashboard = await dashboardService.GetDashboardAsync(caller.HouseholdId, caller.UserId, greetingName, ct);
        return Results.Ok(dashboard);
    }

    /// <summary>
    /// The greeting fallback chain (parity with Home.razor): given name → first word of the full name → the
    /// email local-part → "there". Resolved here (the endpoint holds the principal) so the service stays
    /// claims-free and unit-testable.
    /// </summary>
    private static string ResolveGreetingName(ClaimsPrincipal principal) =>
        principal.FindFirst(ClaimTypes.GivenName)?.Value
        ?? principal.FindFirst(ClaimTypes.Name)?.Value?.Split(' ').FirstOrDefault()
        ?? principal.FindFirst(ClaimTypes.Email)?.Value?.Split('@').FirstOrDefault()
        ?? "there";
}
