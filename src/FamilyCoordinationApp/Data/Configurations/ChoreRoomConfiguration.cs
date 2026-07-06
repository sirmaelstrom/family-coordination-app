using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class ChoreRoomConfiguration : IEntityTypeConfiguration<ChoreRoom>
{
    public void Configure(EntityTypeBuilder<ChoreRoom> builder)
    {
        builder.ToTable("ChoreRooms");

        // Composite PK (HouseholdId first by convention). The full triple is the key, so a
        // (chore, room) membership is unique by construction and a duplicate roomId in a
        // reconcile request would violate the PK (helpers .Distinct()-normalize to prevent it).
        builder.HasKey(x => new { x.HouseholdId, x.ChoreId, x.RoomId });

        // FK (HouseholdId, ChoreId) → Chore. ClientNoAction (=> NO ACTION in Postgres): the DB
        // does NOT cascade, so chore-delete (WP-02) explicitly removes these rows first, else
        // Postgres throws FK 23503. Both FKs share HouseholdId; no nav props (queried explicitly).
        builder.HasOne<Chore>()
            .WithMany()
            .HasForeignKey(x => new { x.HouseholdId, x.ChoreId })
            .OnDelete(DeleteBehavior.ClientNoAction);

        // FK (HouseholdId, RoomId) → Room. ClientNoAction: room-delete (WP-03) explicitly removes
        // these membership rows before removing the room row.
        builder.HasOne<Room>()
            .WithMany()
            .HasForeignKey(x => new { x.HouseholdId, x.RoomId })
            .OnDelete(DeleteBehavior.ClientNoAction);

        // The rollup groups memberships by room within a household. The PK begins (HouseholdId,
        // ChoreId, …) so it can't serve a (HouseholdId, RoomId) lookup — add the covering index.
        builder.HasIndex(x => new { x.HouseholdId, x.RoomId });
    }
}
