using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class HouseholdChoreDigestSettingsConfiguration : IEntityTypeConfiguration<HouseholdChoreDigestSettings>
{
    public void Configure(EntityTypeBuilder<HouseholdChoreDigestSettings> builder)
    {
        builder.ToTable("ChoreDigestSettings");

        // PK = HouseholdId (1:1). Must be ValueGeneratedNever — it is a caller-supplied FK, not a
        // generated identity. Without this EF may refuse to insert a caller-set int PK by convention.
        builder.HasKey(s => s.HouseholdId);
        builder.Property(s => s.HouseholdId).ValueGeneratedNever();

        builder.Property(s => s.WebhookUrlProtected)
            .HasMaxLength(2000);

        builder.Property(s => s.Enabled)
            .IsRequired()
            .HasDefaultValue(false);

        // DigestCadence stored as string (codebase HasConversion<string>() convention).
        builder.Property(s => s.Cadence)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(DigestCadence.Weekly);

        // DayOfWeek stored as int by EF default (BCL enum → underlying int) — intentional per WP-01.
        builder.Property(s => s.SendDayOfWeek)
            .IsRequired()
            .HasDefaultValue(DayOfWeek.Sunday);

        builder.Property(s => s.SendHourLocal)
            .IsRequired()
            .HasDefaultValue(18);

        builder.Property(s => s.LastSentAt);

        builder.Property(s => s.CreatedAt)
            .IsRequired();

        builder.Property(s => s.UpdatedAt)
            .IsRequired();

        // FK to Household — Cascade (matches RoomConfiguration). WithMany() no-arg: do NOT add a
        // nav collection to Household.cs (MN5/E2).
        builder.HasOne(s => s.Household)
            .WithMany()
            .HasForeignKey(s => s.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
