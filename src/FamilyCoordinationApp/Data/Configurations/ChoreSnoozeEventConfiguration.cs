using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class ChoreSnoozeEventConfiguration : IEntityTypeConfiguration<ChoreSnoozeEvent>
{
    public void Configure(EntityTypeBuilder<ChoreSnoozeEvent> builder)
    {
        builder.ToTable("ChoreSnoozeEvents");

        // Composite PK: HouseholdId + ChoreId + SnoozeEventId (mirrors ChoreCompletion / ChoreEvent).
        builder.HasKey(se => new { se.HouseholdId, se.ChoreId, se.SnoozeEventId });
        builder.Property(se => se.SnoozeEventId).ValueGeneratedOnAdd();

        builder.Property(se => se.SetAt)
            .IsRequired();

        // SnoozedUntil: nullable `date` (null = a CLEAR event). No special config — Npgsql maps DateOnly? → date.

        // FK to Chore (composite). Cascade on Chore delete (household-scoped teardown flows Household→Chore→here).
        builder.HasOne(se => se.Chore)
            .WithMany(c => c.SnoozeEvents)
            .HasForeignKey(se => new { se.HouseholdId, se.ChoreId })
            .OnDelete(DeleteBehavior.Cascade);

        // User FK => Restrict: a user delete must not wipe the snooze history (council M4).
        builder.HasOne(se => se.SetBy)
            .WithMany()
            .HasForeignKey(se => se.SetByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
