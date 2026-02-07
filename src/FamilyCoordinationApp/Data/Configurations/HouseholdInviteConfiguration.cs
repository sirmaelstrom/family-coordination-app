using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class HouseholdInviteConfiguration : IEntityTypeConfiguration<HouseholdInvite>
{
    public void Configure(EntityTypeBuilder<HouseholdInvite> builder)
    {
        builder.ToTable("HouseholdInvites");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedOnAdd();

        // Properties
        builder.Property(i => i.InviteCode)
            .IsRequired()
            .HasMaxLength(6)
            .IsFixedLength();

        builder.Property(i => i.CreatedAt)
            .IsRequired();

        builder.Property(i => i.ExpiresAt)
            .IsRequired();

        builder.Property(i => i.IsUsed)
            .IsRequired()
            .HasDefaultValue(false);

        // Concurrency token (xmin)
        builder.Property(i => i.Version)
            .IsRowVersion();

        // Indexes
        builder.HasIndex(i => i.InviteCode)
            .IsUnique();

        builder.HasIndex(i => new { i.HouseholdId, i.IsUsed, i.ExpiresAt });

        // Relationships
        builder.HasOne(i => i.Household)
            .WithMany()
            .HasForeignKey(i => i.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.CreatedBy)
            .WithMany()
            .HasForeignKey(i => i.CreatedByUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.UsedByHousehold)
            .WithMany()
            .HasForeignKey(i => i.UsedByHouseholdId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        builder.HasOne(i => i.UsedByUser)
            .WithMany()
            .HasForeignKey(i => i.UsedByUserId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
    }
}
