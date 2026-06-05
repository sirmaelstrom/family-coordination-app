using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Data.Configurations;

public class ChoreParticipationEventConfiguration : IEntityTypeConfiguration<ChoreParticipationEvent>
{
    public void Configure(EntityTypeBuilder<ChoreParticipationEvent> builder)
    {
        builder.ToTable("ChoreParticipationEvents");

        // Composite PK: HouseholdId + ChoreId + ParticipationEventId (DB-generated, mirroring ChoreEvent).
        builder.HasKey(pe => new { pe.HouseholdId, pe.ChoreId, pe.ParticipationEventId });
        builder.Property(pe => pe.ParticipationEventId).ValueGeneratedOnAdd();

        builder.Property(pe => pe.Type)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(pe => pe.At)
            .IsRequired();

        // FK to Chore (composite). Cascade on Chore delete (household-scoped teardown).
        builder.HasOne(pe => pe.Chore)
            .WithMany(c => c.ParticipationEvents)
            .HasForeignKey(pe => new { pe.HouseholdId, pe.ChoreId })
            .OnDelete(DeleteBehavior.Cascade);

        // User FKs => Restrict: a user delete must not wipe roster history (mirror ChoreEvent, council M4).
        builder.HasOne(pe => pe.Subject)
            .WithMany()
            .HasForeignKey(pe => pe.SubjectUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(pe => pe.Actor)
            .WithMany()
            .HasForeignKey(pe => pe.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Read-path fold scans events per chore in chronological order.
        builder.HasIndex(pe => new { pe.HouseholdId, pe.ChoreId, pe.At });
    }
}
