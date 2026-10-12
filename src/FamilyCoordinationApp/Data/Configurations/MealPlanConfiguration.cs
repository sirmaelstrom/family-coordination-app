using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class MealPlanConfiguration : IEntityTypeConfiguration<MealPlan>
{
    public void Configure(EntityTypeBuilder<MealPlan> builder)
    {
        builder.ToTable("MealPlans");

        // Composite primary key (HouseholdId first by convention)
        builder.HasKey(mp => new { mp.HouseholdId, mp.MealPlanId });
        builder.Property(mp => mp.MealPlanId).ValueGeneratedOnAdd();

        builder.Property(mp => mp.WeekStartDate)
            .IsRequired();

        // One plan per household per week (quest f5ca4b08). Two concurrent first adds into a new week used to commit
        // two plans; now the second insert is a unique violation, and IdGenerationHelper's retry re-reads the week
        // and finds the winner's plan (MealPlanService.AddMealAsync / GetOrCreateMealPlanAsync).
        builder.HasIndex(mp => new { mp.HouseholdId, mp.WeekStartDate })
            .IsUnique();

        builder.Property(mp => mp.CreatedAt)
            .IsRequired();

        // Relationship to Household
        builder.HasOne(mp => mp.Household)
            .WithMany(h => h.MealPlans)
            .HasForeignKey(mp => mp.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
