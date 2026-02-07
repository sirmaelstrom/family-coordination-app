using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class HouseholdConnectionConfiguration : IEntityTypeConfiguration<HouseholdConnection>
{
    public void Configure(EntityTypeBuilder<HouseholdConnection> builder)
    {
        builder.ToTable("HouseholdConnections", t =>
        {
            // Enforce ordered IDs: HouseholdId1 must always be less than HouseholdId2
            t.HasCheckConstraint("CK_HouseholdConnections_OrderedIds",
                "\"HouseholdId1\" < \"HouseholdId2\"");
        });

        // Composite primary key
        builder.HasKey(c => new { c.HouseholdId1, c.HouseholdId2 });

        // Properties
        builder.Property(c => c.ConnectedAt)
            .IsRequired();

        // Indexes for querying connections from either side
        builder.HasIndex(c => c.HouseholdId1);
        builder.HasIndex(c => c.HouseholdId2);

        // Relationships
        builder.HasOne(c => c.Household1)
            .WithMany()
            .HasForeignKey(c => c.HouseholdId1)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.Household2)
            .WithMany()
            .HasForeignKey(c => c.HouseholdId2)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.InitiatedBy)
            .WithMany()
            .HasForeignKey(c => c.InitiatedByUserId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        builder.HasOne(c => c.AcceptedBy)
            .WithMany()
            .HasForeignKey(c => c.AcceptedByUserId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
    }
}
