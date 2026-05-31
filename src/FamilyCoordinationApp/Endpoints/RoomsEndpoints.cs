using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Minimal-API surface for rooms (WP-06). Mirrors <c>ShoppingListEndpoints</c>: a <c>/api/rooms</c> group
/// behind <c>.RequireAuthorization().DisableAntiforgery()</c>, every handler resolving the HouseholdId from
/// the authenticated caller (M1, never client-supplied) via <see cref="UserContextResolver"/>. Room CRUD +
/// reorder delegate to <see cref="IRoomService"/>; photo upload is a dedicated multipart route (council C2).
/// </summary>
public static class RoomsEndpoints
{
    public static IEndpointRouteBuilder MapRoomsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rooms")
            .RequireAuthorization()
            .DisableAntiforgery();

        group.MapGet("/", ListRooms);
        group.MapGet("/{roomId:int}", GetRoom);
        group.MapPost("/", CreateRoom);
        group.MapPut("/{roomId:int}", UpdateRoom);
        group.MapDelete("/{roomId:int}", DeleteRoom);
        group.MapPost("/reorder", ReorderRooms);
        group.MapPost("/{roomId:int}/photo", UploadRoomPhoto);

        return app;
    }

    private static async Task<IResult> ListRooms(
        ClaimsPrincipal principal,
        IRoomService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var rooms = await svc.ListRoomsAsync(user.HouseholdId, ct);
        return Results.Ok(rooms.Select(ToDto).ToList());
    }

    private static async Task<IResult> GetRoom(
        int roomId,
        ClaimsPrincipal principal,
        IRoomService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var room = await svc.GetRoomAsync(user.HouseholdId, roomId, ct);
        return room is null ? Results.NotFound() : Results.Ok(ToDto(room));
    }

    private static async Task<IResult> CreateRoom(
        RoomRequest req,
        ClaimsPrincipal principal,
        IRoomService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { message = "Name is required" });

        var room = await svc.CreateRoomAsync(
            user.HouseholdId, req.Name.Trim(), (req.Icon ?? string.Empty).Trim(), req.PhotoPath, ct);
        return Results.Created($"/api/rooms/{room.RoomId}", ToDto(room));
    }

    private static async Task<IResult> UpdateRoom(
        int roomId,
        RoomRequest req,
        ClaimsPrincipal principal,
        IRoomService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { message = "Name is required" });

        try
        {
            var room = await svc.UpdateRoomAsync(
                user.HouseholdId, roomId, req.Name.Trim(), (req.Icon ?? string.Empty).Trim(), req.PhotoPath, ct);
            return Results.Ok(ToDto(room));
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> DeleteRoom(
        int roomId,
        ClaimsPrincipal principal,
        IRoomService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            await svc.DeleteRoomAsync(user.HouseholdId, roomId, ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> ReorderRooms(
        ReorderRequest req,
        ClaimsPrincipal principal,
        IRoomService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        await svc.ReorderAsync(user.HouseholdId, req.OrderedRoomIds ?? new List<int>(), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UploadRoomPhoto(
        int roomId,
        [FromForm] IFormFile file,
        ClaimsPrincipal principal,
        IImageService imageService,
        IRoomService svc,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();
        if (file is null || file.Length == 0) return Results.BadRequest(new { message = "File is required" });

        // The room must exist + belong to the caller's household before we accept its photo (M1).
        var room = await svc.GetRoomAsync(user.HouseholdId, roomId, ct);
        if (room is null) return Results.NotFound();

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

    private static RoomDto ToDto(Room r) => new(
        r.RoomId,
        r.Name,
        r.Icon,
        r.PhotoPath,
        r.SortOrder);

    public sealed record RoomRequest(string Name, string? Icon, string? PhotoPath);
    public sealed record ReorderRequest(List<int> OrderedRoomIds);

    public sealed record RoomDto(
        int Id,
        string Name,
        string Icon,
        string? PhotoPath,
        int SortOrder);
}
