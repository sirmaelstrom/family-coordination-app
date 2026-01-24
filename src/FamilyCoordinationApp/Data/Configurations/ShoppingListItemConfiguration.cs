using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class ShoppingListItemConfiguration : IEntityTypeConfiguration<ShoppingListItem>
{
    public void Configure(EntityTypeBuilder<ShoppingListItem> builder)
    {
        builder.ToTable("ShoppingListItems");

        // Composite PK: HouseholdId + ShoppingListId + ItemId
        builder.HasKey(sli => new { sli.HouseholdId, sli.ShoppingListId, sli.ItemId });
        builder.Property(sli => sli.ItemId).ValueGeneratedOnAdd();

        builder.Property(sli => sli.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(sli => sli.Unit)
            .HasMaxLength(50);

        builder.Property(sli => sli.Category)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(sli => sli.Quantity)
            .HasPrecision(10, 2);

        builder.Property(sli => sli.IsChecked)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(sli => sli.AddedAt)
            .IsRequired();

        // Consolidation tracking fields
        builder.Property(sli => sli.SourceRecipes)
            .HasMaxLength(500);

        builder.Property(sli => sli.OriginalUnits)
            .HasMaxLength(200);

        builder.Property(sli => sli.IsManuallyAdded)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(sli => sli.QuantityDelta)
            .HasPrecision(10, 2);

        builder.Property(sli => sli.RecipeIngredientIds)
            .HasMaxLength(500);

        builder.Property(sli => sli.SortOrder)
            .IsRequired()
            .HasDefaultValue(0);

        // FK to ShoppingList (composite)
        builder.HasOne(sli => sli.ShoppingList)
            .WithMany(sl => sl.Items)
            .HasForeignKey(sli => new { sli.HouseholdId, sli.ShoppingListId })
            .OnDelete(DeleteBehavior.Cascade);

        // Optional FK to User (simple, nullable)
        builder.HasOne(sli => sli.AddedBy)
            .WithMany()
            .HasForeignKey(sli => sli.AddedByUserId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);
    }
}
