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
            .HasMaxLength(FieldLengths.HouseholdRequest.Email);

        builder.Property(r => r.DisplayName)
            .IsRequired()
            .HasMaxLength(FieldLengths.HouseholdRequest.DisplayName);

        builder.Property(r => r.GoogleId)
            .HasMaxLength(FieldLengths.HouseholdRequest.GoogleId);

        builder.Property(r => r.HouseholdName)
            .IsRequired()
            .HasMaxLength(FieldLengths.HouseholdRequest.HouseholdName);

        builder.Property(r => r.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(FieldLengths.HouseholdRequest.Status);

        builder.Property(r => r.RequestedAt)
            .IsRequired();

        builder.Property(r => r.ReviewedBy)
            .HasMaxLength(FieldLengths.HouseholdRequest.ReviewedBy);

        builder.Property(r => r.RejectionReason)
            .HasMaxLength(FieldLengths.HouseholdRequest.RejectionReason);

        // Index for efficient lookups by email
        builder.HasIndex(r => r.Email);

        // Index for finding pending requests
        builder.HasIndex(r => r.Status);
    }
}
