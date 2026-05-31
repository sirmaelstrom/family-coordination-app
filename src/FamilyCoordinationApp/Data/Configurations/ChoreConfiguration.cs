using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class ChoreConfiguration : IEntityTypeConfiguration<Chore>
{
    public void Configure(EntityTypeBuilder<Chore> builder)
    {
        builder.ToTable("Chores");

        // Composite PK (HouseholdId first by convention)
        builder.HasKey(c => new { c.HouseholdId, c.ChoreId });
        builder.Property(c => c.ChoreId).ValueGeneratedOnAdd();

        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(c => c.Description)
            .HasMaxLength(2000);

        // Enums stored as strings (codebase convention).
        builder.Property(c => c.RecurrenceMode)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(c => c.DaysOfWeek)
            .HasConversion<string>()
            .HasMaxLength(60);

        builder.Property(c => c.EffortTier)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(c => c.EffortPoints)
            .IsRequired()
            .HasDefaultValue(1);

        builder.Property(c => c.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(c => c.AssignmentKind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(AssignmentKind.None);

        builder.Property(c => c.PhotoPath)
            .HasMaxLength(500);

        builder.Property(c => c.CreatedAt)
            .IsRequired();

        // Relationship to Household
        builder.HasOne(c => c.Household)
            .WithMany()
            .HasForeignKey(c => c.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);

        // Optional FK to Room (composite, nullable). The composite FK includes the non-nullable
        // HouseholdId, so EF cannot emit ON DELETE SET NULL; use ClientSetNull (=> NO ACTION in the
        // DB). The service (WP-03) explicitly nulls RoomId before deleting a room (council M3).
        builder.HasOne(c => c.Room)
            .WithMany()
            .HasForeignKey(c => new { c.HouseholdId, c.RoomId })
            .OnDelete(DeleteBehavior.ClientSetNull)
            .IsRequired(false);

        // User FKs => Restrict (NOT Cascade): a user delete must not cascade-wipe chore history
        // (council M4). The HouseholdId cascade FK already covers household teardown.
        builder.HasOne(c => c.EnteredBy)
            .WithMany()
            .HasForeignKey(c => c.EnteredByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.Owner)
            .WithMany()
            .HasForeignKey(c => c.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.HasOne(c => c.Assignee)
            .WithMany()
            .HasForeignKey(c => c.AssigneeUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        // Concurrency token (PostgreSQL xmin) — council M7.
        builder.Property(c => c.Version)
            .IsRowVersion();
    }
}
