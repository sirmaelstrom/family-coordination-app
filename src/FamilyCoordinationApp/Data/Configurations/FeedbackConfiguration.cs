using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class FeedbackConfiguration : IEntityTypeConfiguration<Feedback>
{
    public void Configure(EntityTypeBuilder<Feedback> builder)
    {
        builder.ToTable("Feedback");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedOnAdd();

        builder.Property(f => f.Type)
            .IsRequired()
            .HasConversion<string>();

        builder.Property(f => f.Message)
            .IsRequired()
            .HasMaxLength(4000);

        builder.Property(f => f.CurrentPage)
            .HasMaxLength(500);

        builder.Property(f => f.UserAgent)
            .HasMaxLength(500);

        builder.Property(f => f.AdminNotes)
            .HasMaxLength(2000);

        builder.Property(f => f.CreatedAt)
            .IsRequired();

        builder.Property(f => f.IsRead)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(f => f.IsResolved)
            .IsRequired()
            .HasDefaultValue(false);

        // Optional FK to User
        builder.HasOne(f => f.User)
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        // Optional FK to Household
        builder.HasOne(f => f.Household)
            .WithMany()
            .HasForeignKey(f => f.HouseholdId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        // Index for admin queries
        builder.HasIndex(f => new { f.IsRead, f.IsResolved, f.CreatedAt });
    }
}
