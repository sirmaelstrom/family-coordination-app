using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyCoordinationApp.Data.Configurations;

public class HouseholdCalendarTokenConfiguration : IEntityTypeConfiguration<HouseholdCalendarToken>
{
    /// <summary>At most one active token per (household, member, feed). Named: the rotation retries on it.</summary>
    public const string ActiveTokenIndexName = "IX_HouseholdCalendarTokens_Active_HouseholdId_UserId_Feed";

    public void Configure(EntityTypeBuilder<HouseholdCalendarToken> builder)
    {
        builder.ToTable("HouseholdCalendarTokens");

        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedOnAdd();
        builder.Property(token => token.TokenHash).IsRequired().HasMaxLength(FieldLengths.HouseholdCalendarToken.TokenHash);
        builder.Property(token => token.CreatedAt).IsRequired();
        builder.Property(token => token.Version).IsRowVersion();

        // Enums stored as strings (codebase convention). The migration that added the column filled the rows that
        // existed with 'MealPlan', the only feed there was.
        builder.Property(token => token.Feed)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(FieldLengths.HouseholdCalendarToken.Feed);

        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasIndex(token => new { token.HouseholdId, token.RevokedAt });
        // Postgres treats NULLs as distinct, so this bounds member links only. The legacy shared link (UserId null)
        // has no writer left; the household held at most one when this index replaced the per-household one.
        builder.HasIndex(token => new { token.HouseholdId, token.UserId, token.Feed })
            .IsUnique()
            .HasFilter("\"RevokedAt\" IS NULL")
            .HasDatabaseName(ActiveTokenIndexName);

        builder.HasOne(token => token.Household)
            .WithMany()
            .HasForeignKey(token => token.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(token => token.User)
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
