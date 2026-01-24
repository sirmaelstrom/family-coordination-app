using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class MealPlanEntryConfiguration : IEntityTypeConfiguration<MealPlanEntry>
{
    public void Configure(EntityTypeBuilder<MealPlanEntry> builder)
    {
        builder.ToTable("MealPlanEntries");

        // Composite PK: HouseholdId + MealPlanId + EntryId
        builder.HasKey(e => new { e.HouseholdId, e.MealPlanId, e.EntryId });
        builder.Property(e => e.EntryId).ValueGeneratedOnAdd();

        builder.Property(e => e.Date)
            .IsRequired();

        builder.Property(e => e.MealType)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(e => e.CustomMealName)
            .HasMaxLength(200);

        builder.Property(e => e.Notes)
            .HasMaxLength(500);

        // FK to MealPlan (composite)
        builder.HasOne(e => e.MealPlan)
            .WithMany(mp => mp.Entries)
            .HasForeignKey(e => new { e.HouseholdId, e.MealPlanId })
            .OnDelete(DeleteBehavior.Cascade);

        // Optional FK to Recipe (composite, nullable)
        builder.HasOne(e => e.Recipe)
            .WithMany()
            .HasForeignKey(e => new { e.HouseholdId, e.RecipeId })
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        // Optional FK to User (simple, nullable)
        builder.HasOne(e => e.UpdatedBy)
            .WithMany()
            .HasForeignKey(e => e.UpdatedByUserId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        // Configure xmin concurrency token
        builder.Property(e => e.Version)
            .IsRowVersion();
    }
}
