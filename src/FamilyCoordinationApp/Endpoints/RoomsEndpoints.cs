using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;
using FamilyCoordinationApp.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Minimal-API surface for rooms (WP-06). Mirrors <c>ShoppingListEndpoints</c>: a <c>/api/rooms</c> group
/// behind <c>.RequireAuthorization().DisableAntiforgery()</c>, every handler taking the HouseholdId of the
/// authenticated caller (M1, never client-supplied) as a <see cref="CallerScope"/>. Room CRUD +
/// reorder delegate to <see cref="IRoomService"/>; photo upload is a dedicated multipart route (council C2).
/// </summary>
public static class RoomsEndpoints
{
    public static IEndpointRouteBuilder MapRoomsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rooms")
            .RequireAuthorization()
            .RequireTenant()
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
        CallerScope caller,
        IRoomService svc,
        CancellationToken ct)
    {
        var rooms = await svc.ListRoomsAsync(caller.HouseholdId, ct);
        return Results.Ok(rooms.Select(ToDto).ToList());
    }

    private static async Task<IResult> GetRoom(
        int roomId,
        CallerScope caller,
        IRoomService svc,
        CancellationToken ct)
    {
        var room = await svc.GetRoomAsync(caller.HouseholdId, roomId, ct);
        return room is null ? Results.NotFound() : Results.Ok(ToDto(room));
    }

    private static async Task<IResult> CreateRoom(
        RoomRequest req,
        CallerScope caller,
        IRoomService svc,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { message = "Name is required" });
        if (!ImagePathPolicy.TryNormalize(req.PhotoPath, caller.HouseholdId, out var photoPath))
        {
            return Results.BadRequest(new { message = "Photo path is not valid." });
        }

        var room = await svc.CreateRoomAsync(
            caller.HouseholdId, req.Name.Trim(), (req.Icon ?? string.Empty).Trim(), photoPath, ct);
        return Results.Created($"/api/rooms/{room.RoomId}", ToDto(room));
    }

    private static async Task<IResult> UpdateRoom(
        int roomId,
        RoomRequest req,
        CallerScope caller,
        IRoomService svc,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { message = "Name is required" });
        if (!ImagePathPolicy.TryNormalize(req.PhotoPath, caller.HouseholdId, out var photoPath))
        {
            return Results.BadRequest(new { message = "Photo path is not valid." });
        }

        try
        {
            var room = await svc.UpdateRoomAsync(
                caller.HouseholdId, roomId, req.Name.Trim(), (req.Icon ?? string.Empty).Trim(), photoPath, ct);
            return Results.Ok(ToDto(room));
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> DeleteRoom(
        int roomId,
        CallerScope caller,
        IRoomService svc,
        CancellationToken ct)
    {
        try
        {
            await svc.DeleteRoomAsync(caller.HouseholdId, roomId, ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> ReorderRooms(
        ReorderRequest req,
        CallerScope caller,
        IRoomService svc,
        CancellationToken ct)
    {
        await svc.ReorderAsync(caller.HouseholdId, req.OrderedRoomIds ?? new List<int>(), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UploadRoomPhoto(
        int roomId,
        [FromForm] IFormFile file,
        CallerScope caller,
        IImageService imageService,
        IRoomService svc,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0) return Results.BadRequest(new { message = "File is required" });

        // The room must exist + belong to the caller's household before we accept its photo (M1).
        var room = await svc.GetRoomAsync(caller.HouseholdId, roomId, ct);
        if (room is null) return Results.NotFound();

        try
        {
            var path = await imageService.SaveImageAsync(file, caller.HouseholdId, ct);
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
