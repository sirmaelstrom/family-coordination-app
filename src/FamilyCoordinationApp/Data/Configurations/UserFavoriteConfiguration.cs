using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class UserFavoriteConfiguration : IEntityTypeConfiguration<UserFavorite>
{
    public void Configure(EntityTypeBuilder<UserFavorite> builder)
    {
        builder.ToTable("UserFavorites");

        // Composite PK: UserId + HouseholdId + RecipeId
        builder.HasKey(f => new { f.UserId, f.HouseholdId, f.RecipeId });

        builder.Property(f => f.CreatedAt)
            .IsRequired();

        // FK to User
        builder.HasOne(f => f.User)
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // FK to Recipe (composite)
        builder.HasOne(f => f.Recipe)
            .WithMany()
            .HasForeignKey(f => new { f.HouseholdId, f.RecipeId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
