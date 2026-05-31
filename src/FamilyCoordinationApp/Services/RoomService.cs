using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class RoomService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IImageService imageService,
    ILogger<RoomService> logger) : IRoomService
{
    public async Task<List<Room>> ListRoomsAsync(int householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.Rooms
            .Where(r => r.HouseholdId == householdId)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Room?> GetRoomAsync(int householdId, int roomId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.Rooms
            .FirstOrDefaultAsync(r => r.HouseholdId == householdId && r.RoomId == roomId, cancellationToken);
    }

    public async Task<Room> CreateRoomAsync(int householdId, string name, string icon, string? photoPath = null, CancellationToken cancellationToken = default)
    {
        return await IdGenerationHelper.ExecuteWithRetryAsync(
            async (attempt) =>
            {
                await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

                var maxId = await context.Rooms
                    .Where(r => r.HouseholdId == householdId)
                    .MaxAsync(r => (int?)r.RoomId, cancellationToken) ?? 0;

                var maxSortOrder = await context.Rooms
                    .Where(r => r.HouseholdId == householdId)
                    .MaxAsync(r => (int?)r.SortOrder, cancellationToken) ?? 0;

                var room = new Room
                {
                    HouseholdId = householdId,
                    RoomId = maxId + 1,
                    Name = name.Trim(),
                    Icon = icon.Trim(),
                    PhotoPath = photoPath,
                    SortOrder = maxSortOrder + 1,
                    CreatedAt = DateTime.UtcNow
                };

                context.Rooms.Add(room);
                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Created Room {RoomId} for household {HouseholdId}",
                    room.RoomId, householdId);

                return room;
            },
            logger,
            "Room");
    }

    public async Task<Room> UpdateRoomAsync(int householdId, int roomId, string name, string icon, string? photoPath, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var room = await context.Rooms
            .FirstOrDefaultAsync(r => r.HouseholdId == householdId && r.RoomId == roomId, cancellationToken)
            ?? throw new InvalidOperationException($"Room {roomId} not found for household {householdId}");

        // Delete-on-replace (M8): if the photo path changed and there was an old image, remove the old
        // file from disk. EF does not touch the filesystem. Done before SaveChanges so a failed save does
        // not leave us having deleted a still-referenced file.
        var oldPhotoPath = room.PhotoPath;
        if (!string.Equals(oldPhotoPath, photoPath, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(oldPhotoPath))
        {
            await imageService.DeleteImageAsync(oldPhotoPath, cancellationToken);
        }

        room.Name = name.Trim();
        room.Icon = icon.Trim();
        room.PhotoPath = photoPath;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Updated Room {RoomId} for household {HouseholdId}", roomId, householdId);

        return room;
    }

    public async Task DeleteRoomAsync(int householdId, int roomId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var room = await context.Rooms
            .FirstOrDefaultAsync(r => r.HouseholdId == householdId && r.RoomId == roomId, cancellationToken)
            ?? throw new InvalidOperationException($"Room {roomId} not found for household {householdId}");

        // Explicitly null out RoomId on every chore in this room (same household) BEFORE removing the
        // room row, in the SAME SaveChanges (council M3). The Chore→Room composite FK is ClientSetNull
        // (NO ACTION in the DB), so the database will NOT null these out for us — without this the delete
        // would fail (or orphan a stale RoomId). Chores survive and fall back to "General" (RoomId == null).
        var chores = await context.Chores
            .Where(c => c.HouseholdId == householdId && c.RoomId == roomId)
            .ToListAsync(cancellationToken);

        foreach (var chore in chores)
        {
            chore.RoomId = null;
        }

        // Delete the room's photo from disk before removing the row (M8 — EF cascade does not touch the
        // filesystem). No-op safe when PhotoPath is null/empty (DeleteImageAsync short-circuits).
        if (!string.IsNullOrWhiteSpace(room.PhotoPath))
        {
            await imageService.DeleteImageAsync(room.PhotoPath, cancellationToken);
        }

        context.Rooms.Remove(room);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted Room {RoomId} for household {HouseholdId}; nulled RoomId on {ChoreCount} chore(s)",
            roomId, householdId, chores.Count);
    }

    public async Task ReorderAsync(int householdId, IReadOnlyList<int> orderedRoomIds, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var rooms = await context.Rooms
            .Where(r => r.HouseholdId == householdId && orderedRoomIds.Contains(r.RoomId))
            .ToListAsync(cancellationToken);

        for (var index = 0; index < orderedRoomIds.Count; index++)
        {
            var room = rooms.FirstOrDefault(r => r.RoomId == orderedRoomIds[index]);
            if (room is not null)
            {
                room.SortOrder = index;
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Reordered {Count} room(s) for household {HouseholdId}",
            rooms.Count, householdId);
    }
}
