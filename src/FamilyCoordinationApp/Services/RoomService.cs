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

        // Phase 13 (M:N): remove this room's ChoreRoom membership rows BEFORE the room row, in the SAME
        // SaveChanges (M1, M4 — explicit delete, never DB cascade; the ChoreRoom→Room FK is ClientNoAction).
        // Collect the affected chore ids FIRST (the set is lost once the rows are removed). A chore in other
        // rooms keeps those; a chore left with zero memberships falls back to General.
        var affectedChoreIds = await context.ChoreRooms
            .Where(cr => cr.HouseholdId == householdId && cr.RoomId == roomId)
            .Select(cr => cr.ChoreId)
            .ToListAsync(cancellationToken);

        var membershipsToRemove = await context.ChoreRooms
            .Where(cr => cr.HouseholdId == householdId && cr.RoomId == roomId)
            .ToListAsync(cancellationToken);
        context.ChoreRooms.RemoveRange(membershipsToRemove);

        // Recompute the dual-write shim for each affected chore = the MIN remaining membership (or null =
        // General) — deterministic, matching WP-02's min shim. One batched read (filter RoomId != roomId so
        // the about-to-be-deleted rows are excluded regardless of flush order).
        var remainingMinByChore = (await context.ChoreRooms
            .Where(cr => cr.HouseholdId == householdId && cr.RoomId != roomId && affectedChoreIds.Contains(cr.ChoreId))
            .Select(cr => new { cr.ChoreId, cr.RoomId })
            .ToListAsync(cancellationToken))
            .GroupBy(x => x.ChoreId)
            .ToDictionary(g => g.Key, g => (int?)g.Min(x => x.RoomId));

        var affectedChores = await context.Chores
            .Where(c => c.HouseholdId == householdId && affectedChoreIds.Contains(c.ChoreId))
            .ToListAsync(cancellationToken);

        foreach (var chore in affectedChores)
        {
            chore.RoomId = remainingMinByChore.TryGetValue(chore.ChoreId, out var minRoom) ? minRoom : null;
        }

        // Delete the room's photo from disk before removing the row (M8 — EF cascade does not touch the
        // filesystem). No-op safe when PhotoPath is null/empty (DeleteImageAsync short-circuits).
        if (!string.IsNullOrWhiteSpace(room.PhotoPath))
        {
            await imageService.DeleteImageAsync(room.PhotoPath, cancellationToken);
        }

        context.Rooms.Remove(room);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted Room {RoomId} for household {HouseholdId}; removed membership on {ChoreCount} chore(s)",
            roomId, householdId, affectedChoreIds.Count);
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
