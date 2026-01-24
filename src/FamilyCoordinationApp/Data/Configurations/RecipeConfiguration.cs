using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        builder.ToTable("Recipes");

        // Composite primary key (HouseholdId first by convention)
        builder.HasKey(r => new { r.HouseholdId, r.RecipeId });

        // Auto-increment RecipeId within household context
        builder.Property(r => r.RecipeId).ValueGeneratedOnAdd();

        // Properties
        builder.Property(r => r.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(r => r.Description)
            .HasMaxLength(1000);

        builder.Property(r => r.Instructions)
            .HasMaxLength(10000);

        builder.Property(r => r.ImagePath)
            .HasMaxLength(500);

        builder.Property(r => r.SourceUrl)
            .HasMaxLength(2000);

        builder.Property(r => r.IsDeleted)
            .IsRequired()
            .HasDefaultValue(false);

        // Query filter for soft delete
        builder.HasQueryFilter(r => !r.IsDeleted);

        // Indexes
        builder.HasIndex(r => new { r.HouseholdId, r.Name });

        // Relationships
        builder.HasOne(r => r.Household)
            .WithMany(h => h.Recipes)
            .HasForeignKey(r => r.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.CreatedBy)
            .WithMany()
            .HasForeignKey(r => r.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.UpdatedBy)
            .WithMany()
            .HasForeignKey(r => r.UpdatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Configure xmin concurrency token
        builder.Property(r => r.Version)
            .IsRowVersion();
    }
}
