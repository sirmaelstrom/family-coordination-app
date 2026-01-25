using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class HouseholdRequestConfiguration : IEntityTypeConfiguration<HouseholdRequest>
{
    public void Configure(EntityTypeBuilder<HouseholdRequest> builder)
    {
        builder.ToTable("HouseholdRequests");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedOnAdd();

        builder.Property(r => r.Email)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(r => r.DisplayName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(r => r.GoogleId)
            .HasMaxLength(200);

        builder.Property(r => r.HouseholdName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(r => r.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(r => r.RequestedAt)
            .IsRequired();

        builder.Property(r => r.ReviewedBy)
            .HasMaxLength(256);

        builder.Property(r => r.RejectionReason)
            .HasMaxLength(500);

        // Index for efficient lookups by email
        builder.HasIndex(r => r.Email);
        
        // Index for finding pending requests
        builder.HasIndex(r => r.Status);
    }
}
