using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Minimal-API surface for chores + the board (WP-06). Mirrors <c>ShoppingListEndpoints</c>: a
/// <c>/api/chores</c> group behind <c>.RequireAuthorization().DisableAntiforgery()</c>, every handler
/// resolving the HouseholdId/UserId from the authenticated caller (M1, never client-supplied) via
/// <see cref="UserContextResolver"/>. Writes delegate to <see cref="IChoreService"/>; the board read +
/// the per-mutation response projection delegate to <see cref="IChoreBoardService"/> (ONE projection — no
/// card/mutation-response drift, M9). The service's typed exceptions map to HTTP status:
/// <see cref="ChoreConflictException"/> → 409 (xmin conflict, M7/M12),
/// <see cref="ChoreValidationException"/> → 400 (illegal transition / bad input, MN8),
/// <see cref="ChoreNotFoundException"/> → 404 (also covers cross-household access, M1).
/// </summary>
public static class ChoresEndpoints
{
    public static IEndpointRouteBuilder MapChoresEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chores")
            .RequireAuthorization()
            .DisableAntiforgery();

        group.MapGet("/board", GetBoard);

        group.MapPost("/", CreateChore);
        group.MapPut("/{choreId:int}", UpdateChore);
        group.MapDelete("/{choreId:int}", DeleteChore);

        group.MapPost("/{choreId:int}/claim", ClaimChore);
        group.MapPost("/{choreId:int}/drop", DropChore);
        group.MapPost("/{choreId:int}/handoff", HandOffChore);
        group.MapPost("/{choreId:int}/complete", CompleteChore);
        group.MapPost("/{choreId:int}/photo", UploadChorePhoto);

        group.MapPatch("/me/default-view", SetDefaultView);

        return app;
    }

    // ─── Board ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetBoard(
        ClaimsPrincipal principal,
        IChoreBoardService boardService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var board = await boardService.GetBoardAsync(user.HouseholdId, user.UserId, null, ct);
        return Results.Ok(board);
    }

    // ─── Chore CRUD ───────────────────────────────────────────────────────────────

    private static async Task<IResult> CreateChore(
        CreateChoreRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.CreateChoreAsync(user.HouseholdId, user.UserId, req.ToCommand(), ct);
            return Results.Created($"/api/chores/{chore.ChoreId}", Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreValidationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> UpdateChore(
        int choreId,
        UpdateChoreRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.UpdateChoreAsync(user.HouseholdId, choreId, req.ToCommand(), req.Version, ct);
            return Results.Ok(Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreValidationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    private static async Task<IResult> DeleteChore(
        int choreId,
        [FromBody] VersionRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            await svc.DeleteChoreAsync(user.HouseholdId, choreId, req.Version, ct);
            return Results.NoContent();
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    // ─── Claim state machine ──────────────────────────────────────────────────────

    private static async Task<IResult> ClaimChore(
        int choreId,
        VersionRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.ClaimAsync(user.HouseholdId, choreId, user.UserId, req.Version, ct);
            return Results.Ok(Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreValidationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    private static async Task<IResult> DropChore(
        int choreId,
        VersionRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.DropAsync(user.HouseholdId, choreId, user.UserId, req.Version, ct);
            return Results.Ok(Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreValidationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    private static async Task<IResult> HandOffChore(
        int choreId,
        HandOffRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.HandOffAsync(user.HouseholdId, choreId, user.UserId, req.TargetUserId, req.Version, ct);
            return Results.Ok(Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreValidationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    private static async Task<IResult> CompleteChore(
        int choreId,
        CompleteRequest req,
        ClaimsPrincipal principal,
        IChoreService svc,
        IChoreBoardService boardService,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            var chore = await svc.CompleteAsync(
                user.HouseholdId, choreId, user.UserId, req.Note, req.PhotoPath, req.Version, ct);
            return Results.Ok(Project(boardService, chore, timeProvider, timeZone));
        }
        catch (ChoreNotFoundException) { return Results.NotFound(); }
        catch (ChoreValidationException ex) { return Results.BadRequest(new { message = ex.Message }); }
        catch (ChoreConflictException ex) { return Results.Conflict(new { message = ex.Message }); }
    }

    // ─── Photo upload (dedicated multipart route, council C2) ───────────────────────

    private static async Task<IResult> UploadChorePhoto(
        int choreId,
        [FromForm] IFormFile file,
        ClaimsPrincipal principal,
        IImageService imageService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();
        if (file is null || file.Length == 0) return Results.BadRequest(new { message = "File is required" });

        try
        {
            var path = await imageService.SaveImageAsync(file, user.HouseholdId, ct);
            return Results.Ok(new { photoPath = path });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    // ─── Per-user default lens (D18, council M7 pinned route) ───────────────────────

    private static async Task<IResult> SetDefaultView(
        DefaultViewRequest req,
        ClaimsPrincipal principal,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var (outcome, normalized) = await ApplyDefaultViewAsync(dbFactory, user.UserId, req.View, ct);
        return outcome switch
        {
            DefaultViewOutcome.Ok => Results.Ok(new { view = normalized }),
            DefaultViewOutcome.InvalidLens => Results.BadRequest(
                new { message = $"Unknown lens id '{req.View}'. Valid: {string.Join(", ", ChoreLens.All)}" }),
            _ => Results.Unauthorized()
        };
    }

    internal enum DefaultViewOutcome { Ok, InvalidLens, UserMissing }

    /// <summary>
    /// Core of <see cref="SetDefaultView"/>, extracted so it is unit-testable without a WebApplicationFactory
    /// (council M10). null/blank clears the preference to the default (Needs-attention); a non-null value MUST
    /// be a canonical lens id (council M6 — anything else is rejected, never coerced, MN8); the write is scoped
    /// to <paramref name="userId"/> only (the resolved caller, M1).
    /// </summary>
    internal static async Task<(DefaultViewOutcome Outcome, string? Normalized)> ApplyDefaultViewAsync(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        int userId,
        string? requestedView,
        CancellationToken ct)
    {
        string? view = string.IsNullOrWhiteSpace(requestedView) ? null : requestedView.Trim();
        if (view is not null && !ChoreLens.All.Contains(view))
        {
            return (DefaultViewOutcome.InvalidLens, null);
        }

        await using var context = await dbFactory.CreateDbContextAsync(ct);
        var entity = await context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (entity is null) return (DefaultViewOutcome.UserMissing, null);

        entity.ChoresDefaultView = view;
        await context.SaveChangesAsync(ct);

        return (DefaultViewOutcome.Ok, view);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────

    private static ChoreDto Project(
        IChoreBoardService boardService, Chore chore, TimeProvider timeProvider, TimeZoneInfo timeZone) =>
        boardService.ProjectChore(chore, timeProvider.GetUtcNow().UtcDateTime, timeZone);

    // ─── Request DTOs ───────────────────────────────────────────────────────────────

    /// <summary>The client's xmin token for an optimistic-concurrency-checked mutation (M7/M12).</summary>
    public sealed record VersionRequest(uint Version);

    public sealed record HandOffRequest(int? TargetUserId, uint Version);

    public sealed record CompleteRequest(string? Note, string? PhotoPath, uint Version);

    public sealed record DefaultViewRequest(string? View);

    public sealed record CreateChoreRequest(
        string Name,
        string? Description,
        int? RoomId,
        RecurrenceMode RecurrenceMode,
        int? IntervalDays,
        DateOnly? AnchorDate,
        ChoreDaysOfWeek? DaysOfWeek,
        int? DayOfMonth,
        EffortTier EffortTier,
        int? OwnerUserId,
        int? AssigneeUserId,
        string? PhotoPath)
    {
        public CreateChoreCommand ToCommand() => new(
            Name,
            Description,
            RoomId,
            RecurrenceMode,
            IntervalDays,
            AnchorDate,
            DaysOfWeek,
            DayOfMonth,
            EffortTier,
            OwnerUserId,
            AssigneeUserId,
            PhotoPath);
    }

    public sealed record UpdateChoreRequest(
        string Name,
        string? Description,
        int? RoomId,
        RecurrenceMode RecurrenceMode,
        int? IntervalDays,
        DateOnly? AnchorDate,
        ChoreDaysOfWeek? DaysOfWeek,
        int? DayOfMonth,
        EffortTier EffortTier,
        int? OwnerUserId,
        string? PhotoPath,
        uint Version)
    {
        public UpdateChoreCommand ToCommand() => new(
            Name,
            Description,
            RoomId,
            RecurrenceMode,
            IntervalDays,
            AnchorDate,
            DaysOfWeek,
            DayOfMonth,
            EffortTier,
            OwnerUserId,
            PhotoPath);
    }
}
