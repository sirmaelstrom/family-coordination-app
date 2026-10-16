using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Calendar;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tenancy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Extensions;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Authenticated management of the caller's own meal-plan calendar capability, and its anonymous feed. Each member
/// has their own link (quest 8dba87f4): create, rotate and revoke touch only the caller's link. The legacy
/// household-shared link, if one is still active, is reported and can be revoked, never rotated.
/// </summary>
public static class CalendarTokenEndpoints
{
    public static IEndpointRouteBuilder MapCalendarTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var management = app.MapGroup("/api/meal-plan/calendar-token")
            // Login: the /api gate (the FallbackPolicy in Program.cs), not a per-group RequireAuthorization().
            .RequireTenant()
            .DisableAntiforgery();

        management.MapPost("", CreateOrRotate);
        management.MapDelete("", Revoke);
        management.MapDelete("/shared", RevokeShared);
        management.MapGet("", GetStatus);
        app.MapGet("/api/calendar/meal-plan.ics", GetFeed).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> CreateOrRotate(
        HttpContext httpContext,
        CallerScope caller,
        IHouseholdCalendarTokenService tokenService,
        CancellationToken ct)
    {
        var created = await tokenService.CreateOrRotateAsync(caller.HouseholdId, caller.UserId, CalendarFeed.MealPlan, ct);
        var url = UriHelper.BuildAbsolute(
            httpContext.Request.Scheme,
            httpContext.Request.Host,
            httpContext.Request.PathBase,
            "/api/calendar/meal-plan.ics",
            QueryString.Create("token", created.Token));
        return Results.Ok(new { token = created.Token, url });
    }

    private static async Task<IResult> Revoke(
        CallerScope caller,
        IHouseholdCalendarTokenService tokenService,
        CancellationToken ct)
    {
        await tokenService.RevokeAsync(caller.HouseholdId, caller.UserId, CalendarFeed.MealPlan, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeShared(
        CallerScope caller,
        IHouseholdCalendarTokenService tokenService,
        CancellationToken ct)
    {
        await tokenService.RevokeSharedAsync(caller.HouseholdId, CalendarFeed.MealPlan, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetStatus(
        CallerScope caller,
        IHouseholdCalendarTokenService tokenService,
        CancellationToken ct)
    {
        var active = await tokenService.GetActiveAsync(caller.HouseholdId, caller.UserId, CalendarFeed.MealPlan, ct);
        var shared = await tokenService.GetActiveSharedAsync(caller.HouseholdId, CalendarFeed.MealPlan, ct);
        return Results.Ok(new
        {
            active = active is not null,
            createdAt = active?.CreatedAt,
            sharedLinkActive = shared is not null,
            sharedLinkCreatedAt = shared?.CreatedAt,
        });
    }

    private static async Task<IResult> GetFeed(
        string? token,
        IHouseholdCalendarTokenService tokenService,
        IMealPlanService mealPlanService,
        IMealPlanBoardService boardService,
        ICalendarWriter calendarWriter,
        TimeProvider timeProvider,
        IHouseholdClock clock,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var calendarToken = await tokenService.ResolveActiveAsync(token, CalendarFeed.MealPlan, ct);
        if (calendarToken is null) return CalendarNotFoundResult.Instance;

        var nowUtc = timeProvider.GetUtcNow();
        var localToday = clock.Today(calendarToken.HouseholdId);
        var weekStart = mealPlanService.GetWeekStartDate(localToday);
        // The anonymous feed has no caller: the token's household is the tenant (D11). The scope wraps the whole
        // WhenAll, so all four concurrent loads read it; nothing inside changes the tenant.
        MealPlanBoardDto[] boards;
        using (tenant.RunAs(calendarToken.HouseholdId))
        {
            boards = await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(offset => boardService.GetBoardAsync(calendarToken.HouseholdId, weekStart.AddDays(offset * 7), ct)));
        }
        var content = calendarWriter.WriteMealPlan(calendarToken.HouseholdId, weekStart, boards.SelectMany(board => board.Entries), nowUtc);
        return new CalendarResult(content);
    }

    private sealed class CalendarResult(string content) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = "text/calendar; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "private, no-store";
            return httpContext.Response.WriteAsync(content);
        }
    }

    private sealed class CalendarNotFoundResult : IResult
    {
        public static readonly CalendarNotFoundResult Instance = new();

        public Task ExecuteAsync(HttpContext httpContext)
        {
            var statusCodePages = httpContext.Features.Get<IStatusCodePagesFeature>();
            if (statusCodePages is not null) statusCodePages.Enabled = false;
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
    }
}
