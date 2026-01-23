using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedOnAdd();

        builder.Property(u => u.Email)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(u => u.DisplayName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(u => u.GoogleId)
            .IsRequired()
            .HasMaxLength(200);

        // Unique index on email
        builder.HasIndex(u => u.Email)
            .IsUnique();

        // Unique index on GoogleId
        builder.HasIndex(u => u.GoogleId)
            .IsUnique();

        // Relationship to Household
        builder.HasOne(u => u.Household)
            .WithMany(h => h.Users)
            .HasForeignKey(u => u.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
