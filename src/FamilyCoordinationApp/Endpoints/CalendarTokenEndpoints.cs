using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Services.Calendar;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>Authenticated management of the household calendar capability and its anonymous feed.</summary>
public static class CalendarTokenEndpoints
{
    public static IEndpointRouteBuilder MapCalendarTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var management = app.MapGroup("/api/meal-plan/calendar-token")
            .RequireAuthorization()
            .DisableAntiforgery();

        management.MapPost("", CreateOrRotate);
        management.MapDelete("", Revoke);
        management.MapGet("", GetStatus);
        app.MapGet("/api/calendar/meal-plan.ics", GetFeed).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> CreateOrRotate(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHouseholdCalendarTokenService tokenService,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var created = await tokenService.CreateOrRotateAsync(user.HouseholdId, ct);
        var url = UriHelper.BuildAbsolute(
            httpContext.Request.Scheme,
            httpContext.Request.Host,
            httpContext.Request.PathBase,
            "/api/calendar/meal-plan.ics",
            QueryString.Create("token", created.Token));
        return Results.Ok(new { token = created.Token, url });
    }

    private static async Task<IResult> Revoke(
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHouseholdCalendarTokenService tokenService,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        await tokenService.RevokeAsync(user.HouseholdId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetStatus(
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IHouseholdCalendarTokenService tokenService,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var active = await tokenService.GetActiveAsync(user.HouseholdId, ct);
        return Results.Ok(new { active = active is not null, createdAt = active?.CreatedAt });
    }

    private static async Task<IResult> GetFeed(
        string? token,
        IHouseholdCalendarTokenService tokenService,
        IMealPlanService mealPlanService,
        IMealPlanBoardService boardService,
        ICalendarWriter calendarWriter,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        CancellationToken ct)
    {
        var calendarToken = await tokenService.ResolveActiveAsync(token, ct);
        if (calendarToken is null) return CalendarNotFoundResult.Instance;

        var nowUtc = timeProvider.GetUtcNow();
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime);
        var weekStart = mealPlanService.GetWeekStartDate(localToday);
        var boards = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(offset => boardService.GetBoardAsync(calendarToken.HouseholdId, weekStart.AddDays(offset * 7), ct)));
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
