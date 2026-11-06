using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FamilyCoordinationApp.Data.Configurations;

public class AgentOperationConfiguration : IEntityTypeConfiguration<AgentOperation>
{
    /// <summary>One record per (household, member, request id). Named: the runner tells a replay from a fault by it.</summary>
    public const string RequestIndexName = "IX_AgentOperations_HouseholdId_UserId_RequestId";

    public void Configure(EntityTypeBuilder<AgentOperation> builder)
    {
        builder.ToTable("AgentOperations");

        builder.HasKey(operation => operation.Id);
        builder.Property(operation => operation.Id).ValueGeneratedOnAdd();
        builder.Property(operation => operation.RequestId).IsRequired().HasMaxLength(FieldLengths.AgentOperation.RequestId);
        builder.Property(operation => operation.Tool).IsRequired().HasMaxLength(FieldLengths.AgentOperation.Tool);
        builder.Property(operation => operation.PayloadHash).IsRequired().HasMaxLength(FieldLengths.AgentOperation.PayloadHash);
        builder.Property(operation => operation.ResultJson).IsRequired();
        builder.Property(operation => operation.CreatedAt).IsRequired();

        builder.HasIndex(operation => new { operation.HouseholdId, operation.UserId, operation.RequestId })
            .IsUnique()
            .HasDatabaseName(RequestIndexName);

        builder.HasOne(operation => operation.Household)
            .WithMany()
            .HasForeignKey(operation => operation.HouseholdId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(operation => operation.User)
            .WithMany()
            .HasForeignKey(operation => operation.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
