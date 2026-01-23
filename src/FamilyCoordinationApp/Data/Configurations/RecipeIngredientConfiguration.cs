using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class RecipeIngredientConfiguration : IEntityTypeConfiguration<RecipeIngredient>
{
    public void Configure(EntityTypeBuilder<RecipeIngredient> builder)
    {
        builder.ToTable("RecipeIngredients");

        // Composite PK: HouseholdId + RecipeId + IngredientId
        builder.HasKey(ri => new { ri.HouseholdId, ri.RecipeId, ri.IngredientId });
        builder.Property(ri => ri.IngredientId).ValueGeneratedOnAdd();

        builder.Property(ri => ri.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(ri => ri.Unit)
            .HasMaxLength(50);

        builder.Property(ri => ri.Category)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(ri => ri.Quantity)
            .HasPrecision(10, 2);

        // Composite FK to Recipe (HouseholdId + RecipeId)
        builder.HasOne(ri => ri.Recipe)
            .WithMany(r => r.Ingredients)
            .HasForeignKey(ri => new { ri.HouseholdId, ri.RecipeId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
