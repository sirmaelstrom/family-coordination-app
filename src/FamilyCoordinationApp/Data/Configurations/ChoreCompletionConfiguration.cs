using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class ChoreCompletionConfiguration : IEntityTypeConfiguration<ChoreCompletion>
{
    public void Configure(EntityTypeBuilder<ChoreCompletion> builder)
    {
        builder.ToTable("ChoreCompletions");

        // Composite PK: HouseholdId + ChoreId + CompletionId
        builder.HasKey(cc => new { cc.HouseholdId, cc.ChoreId, cc.CompletionId });
        builder.Property(cc => cc.CompletionId).ValueGeneratedOnAdd();

        builder.Property(cc => cc.CompletedAt)
            .IsRequired();

        builder.Property(cc => cc.EffortPointsSnapshot)
            .IsRequired();

        builder.Property(cc => cc.Note)
            .HasMaxLength(2000);

        builder.Property(cc => cc.PhotoPath)
            .HasMaxLength(500);

        // FK to Chore (composite). Cascade on Chore delete (household-scoped teardown).
        builder.HasOne(cc => cc.Chore)
            .WithMany(c => c.Completions)
            .HasForeignKey(cc => new { cc.HouseholdId, cc.ChoreId })
            .OnDelete(DeleteBehavior.Cascade);

        // User FK => Restrict: a user delete must not wipe the completion history (council M4).
        builder.HasOne(cc => cc.CompletedBy)
            .WithMany()
            .HasForeignKey(cc => cc.CompletedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
