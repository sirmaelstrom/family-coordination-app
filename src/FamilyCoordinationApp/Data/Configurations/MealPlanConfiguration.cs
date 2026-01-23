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

        builder.Property(mp => mp.CreatedAt)
            .IsRequired();

        // Relationship to Household
        builder.HasOne(mp => mp.Household)
            .WithMany(h => h.MealPlans)
            .HasForeignKey(mp => mp.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
