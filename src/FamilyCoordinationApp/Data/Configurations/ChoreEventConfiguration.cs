using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class ChoreEventConfiguration : IEntityTypeConfiguration<ChoreEvent>
{
    public void Configure(EntityTypeBuilder<ChoreEvent> builder)
    {
        builder.ToTable("ChoreEvents");

        // Composite PK: HouseholdId + ChoreId + EventId
        builder.HasKey(ce => new { ce.HouseholdId, ce.ChoreId, ce.EventId });
        builder.Property(ce => ce.EventId).ValueGeneratedOnAdd();

        builder.Property(ce => ce.Type)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(ce => ce.At)
            .IsRequired();

        // FK to Chore (composite). Cascade on Chore delete (household-scoped teardown).
        builder.HasOne(ce => ce.Chore)
            .WithMany(c => c.Events)
            .HasForeignKey(ce => new { ce.HouseholdId, ce.ChoreId })
            .OnDelete(DeleteBehavior.Cascade);

        // User FKs => Restrict: a user delete must not wipe the event history (council M4).
        builder.HasOne(ce => ce.Actor)
            .WithMany()
            .HasForeignKey(ce => ce.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(ce => ce.Target)
            .WithMany()
            .HasForeignKey(ce => ce.TargetUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);
    }
}
