using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.HasKey(c => new { c.HouseholdId, c.CategoryId });

        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(c => c.IconEmoji)
            .HasMaxLength(30);  // Emoji shortcode names like "cup_with_straw"

        builder.Property(c => c.Color)
            .HasMaxLength(7);  // #FFFFFF format

        builder.HasQueryFilter(c => !c.IsDeleted);

        builder.HasOne(c => c.Household)
            .WithMany()
            .HasForeignKey(c => c.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.HouseholdId, c.Name })
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");
    }
}
