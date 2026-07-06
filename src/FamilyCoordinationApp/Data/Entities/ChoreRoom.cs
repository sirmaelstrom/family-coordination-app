namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// Join entity for the chore↔room M:N membership (Phase 13). A chore belongs to 0..N rooms;
/// zero rows == the virtual "General" group. Composite PK (all three columns non-null) doubles as
/// the uniqueness guarantee. No navigation properties — memberships are queried explicitly through
/// <see cref="FamilyCoordinationApp.Services.ChoreRoomMembership"/>, matching the rest of the model.
/// </summary>
public class ChoreRoom
{
    public int HouseholdId { get; set; }
    public int ChoreId { get; set; }
    public int RoomId { get; set; }
}
