using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyCoordinationApp.Data.Configurations;

public class HouseholdCalendarTokenConfiguration : IEntityTypeConfiguration<HouseholdCalendarToken>
{
    public void Configure(EntityTypeBuilder<HouseholdCalendarToken> builder)
    {
        builder.ToTable("HouseholdCalendarTokens");

        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedOnAdd();
        builder.Property(token => token.TokenHash).IsRequired().HasMaxLength(64);
        builder.Property(token => token.CreatedAt).IsRequired();
        builder.Property(token => token.Version).IsRowVersion();

        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasIndex(token => new { token.HouseholdId, token.RevokedAt });

        builder.HasOne(token => token.Household)
            .WithMany()
            .HasForeignKey(token => token.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
