using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class RecipeDraftConfiguration : IEntityTypeConfiguration<RecipeDraft>
{
    public void Configure(EntityTypeBuilder<RecipeDraft> builder)
    {
        // Composite key: one draft per user per recipe (or new recipe)
        builder.HasKey(d => new { d.HouseholdId, d.UserId, d.RecipeId });

        builder.Property(d => d.DraftJson)
            .IsRequired();

        builder.HasOne(d => d.Household)
            .WithMany()
            .HasForeignKey(d => d.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(d => d.User)
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .HasPrincipalKey(u => u.Id)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
