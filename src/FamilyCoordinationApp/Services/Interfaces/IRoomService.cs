using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IRoomService
{
    /// <summary>
    /// Lists all rooms for a household, ordered by SortOrder then Name.
    /// </summary>
    Task<List<Room>> ListRoomsAsync(int householdId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single room scoped to the household, or null if not found.
    /// </summary>
    Task<Room?> GetRoomAsync(int householdId, int roomId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a room for the household. The new room is appended to the end of the sort order.
    /// </summary>
    Task<Room> CreateRoomAsync(int householdId, string name, string icon, string? photoPath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a room's name, icon and photo. When <paramref name="photoPath"/> differs from the stored
    /// value, the previously stored image file is deleted from disk (delete-on-replace, M8).
    /// </summary>
    Task<Room> UpdateRoomAsync(int householdId, int roomId, string name, string icon, string? photoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a room. Before removing the row, the room's <c>ChoreRoom</c> membership rows (same household)
    /// are removed in the same SaveChanges — explicit, because the join FK is <c>ClientNoAction</c> (M1/M4,
    /// Phase 13) — and the room's photo file is deleted from disk (M8). No-op safe when the room has no photo.
    /// A chore in other rooms keeps them; a chore left with zero memberships falls back to "General".
    /// </summary>
    Task DeleteRoomAsync(int householdId, int roomId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reorders rooms within a household. <paramref name="orderedRoomIds"/> is the desired ordering;
    /// each listed room's SortOrder is set to its index. Ids not belonging to the household are ignored.
    /// </summary>
    Task ReorderAsync(int householdId, IReadOnlyList<int> orderedRoomIds, CancellationToken cancellationToken = default);
}
