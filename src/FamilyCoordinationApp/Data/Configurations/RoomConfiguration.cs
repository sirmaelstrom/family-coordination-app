using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class RoomConfiguration : IEntityTypeConfiguration<Room>
{
    public void Configure(EntityTypeBuilder<Room> builder)
    {
        builder.ToTable("Rooms");

        // Composite PK (HouseholdId first by convention)
        builder.HasKey(r => new { r.HouseholdId, r.RoomId });
        builder.Property(r => r.RoomId).ValueGeneratedOnAdd();

        builder.Property(r => r.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(r => r.Icon)
            .IsRequired()
            .HasMaxLength(30);

        builder.Property(r => r.PhotoPath)
            .HasMaxLength(500);

        builder.Property(r => r.SortOrder)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(r => r.CreatedAt)
            .IsRequired();

        // Relationship to Household
        builder.HasOne(r => r.Household)
            .WithMany()
            .HasForeignKey(r => r.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
