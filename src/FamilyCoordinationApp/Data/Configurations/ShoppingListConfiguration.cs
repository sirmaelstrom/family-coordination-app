using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class ShoppingListConfiguration : IEntityTypeConfiguration<ShoppingList>
{
    public void Configure(EntityTypeBuilder<ShoppingList> builder)
    {
        builder.ToTable("ShoppingLists");

        // Composite primary key (HouseholdId first by convention)
        builder.HasKey(sl => new { sl.HouseholdId, sl.ShoppingListId });
        builder.Property(sl => sl.ShoppingListId).ValueGeneratedOnAdd();

        builder.Property(sl => sl.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(sl => sl.CreatedAt)
            .IsRequired();

        builder.Property(sl => sl.IsArchived)
            .IsRequired()
            .HasDefaultValue(false);

        // Relationship to Household
        builder.HasOne(sl => sl.Household)
            .WithMany(h => h.ShoppingLists)
            .HasForeignKey(sl => sl.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);

        // Optional relationship to MealPlan (composite FK)
        builder.HasOne(sl => sl.MealPlan)
            .WithMany()
            .HasForeignKey(sl => new { sl.HouseholdId, sl.MealPlanId })
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
    }
}
