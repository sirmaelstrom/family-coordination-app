using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

/// <summary>
/// EF config for the per-chore checklist (Phase 14). Composite PK <c>(HouseholdId, ChoreId, SubtaskId)</c>
/// with a DB-generated <c>SubtaskId</c> identity (mirroring <c>ChoreParticipationEvent</c>). The relationship
/// to <see cref="Chore"/> rides the composite FK <c>(HouseholdId, ChoreId)</c> and cascades on chore delete.
/// Deliberately carries NO row-version / concurrency token: subtask writes are last-write-wins and must never
/// touch <c>Chore.Version</c> (D-Phase14).
/// </summary>
public class ChoreSubtaskConfiguration : IEntityTypeConfiguration<ChoreSubtask>
{
    public void Configure(EntityTypeBuilder<ChoreSubtask> builder)
    {
        builder.ToTable("ChoreSubtasks");

        // Composite PK: HouseholdId + ChoreId + SubtaskId (DB-generated, mirroring ChoreParticipationEvent).
        builder.HasKey(s => new { s.HouseholdId, s.ChoreId, s.SubtaskId });
        builder.Property(s => s.SubtaskId).ValueGeneratedOnAdd();

        builder.Property(s => s.Title)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(s => s.IsDone)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(s => s.SortOrder)
            .IsRequired();

        builder.Property(s => s.CreatedAt)
            .IsRequired();

        // FK to Chore (composite). Cascade on Chore delete (household-scoped teardown).
        builder.HasOne(s => s.Chore)
            .WithMany(c => c.Subtasks)
            .HasForeignKey(s => new { s.HouseholdId, s.ChoreId })
            .OnDelete(DeleteBehavior.Cascade);

        // NO concurrency token (last-write-wins, never bumps Chore.Version).
    }
}
