using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyCoordinationApp.Data.Configurations;

public class PersonalAccessTokenConfiguration : IEntityTypeConfiguration<PersonalAccessToken>
{
    public void Configure(EntityTypeBuilder<PersonalAccessToken> builder)
    {
        builder.ToTable("PersonalAccessTokens");

        builder.HasKey(token => token.Id);
        builder.Property(token => token.Id).ValueGeneratedOnAdd();
        builder.Property(token => token.Label).IsRequired().HasMaxLength(FieldLengths.PersonalAccessToken.Label);
        builder.Property(token => token.TokenHash).IsRequired().HasMaxLength(FieldLengths.PersonalAccessToken.TokenHash);
        builder.Property(token => token.SafePrefix).IsRequired().HasMaxLength(FieldLengths.PersonalAccessToken.SafePrefix);
        builder.Property(token => token.Access)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(FieldLengths.PersonalAccessToken.Access);
        builder.Property(token => token.CreatedAt).IsRequired();
        builder.Property(token => token.ExpiresAt).IsRequired();

        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasIndex(token => new { token.HouseholdId, token.UserId });

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
