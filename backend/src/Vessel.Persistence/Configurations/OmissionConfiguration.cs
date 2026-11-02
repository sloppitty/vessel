using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Configurations;

/// <summary>Phase 2, item 7: durable omission markers, beside imported fills and never one of them.</summary>
internal sealed class ExecutionOmissionConfiguration : IEntityTypeConfiguration<ExecutionOmission>
{
    public void Configure(EntityTypeBuilder<ExecutionOmission> builder)
    {
        builder.ToTable("execution_omissions", table =>
        {
            table.HasCheckConstraint("CK_execution_omissions_kind", "\"Kind\" IN ('forced', 'fee', 'missing-pnl', 'unrecognized-market')");
            table.HasCheckConstraint("CK_execution_omissions_time",
                """
                "FirstSeenAtUtc" <= "LastSeenAtUtc" AND ("OccurredAtUtc" IS NULL OR "UncertainFromUtc" IS NULL AND "UncertainToUtc" IS NULL) AND
                ("UncertainFromUtc" IS NULL OR "UncertainToUtc" IS NULL OR "UncertainFromUtc" <= "UncertainToUtc")
                """);
        });
        builder.HasKey(x => x.Id);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.Kind, x.SourceId }).IsUnique();
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.OrderId });
        builder.Property(x => x.Kind).HasMaxLength(32);
        builder.Property(x => x.Reason).HasMaxLength(100);
        builder.Property(x => x.SourceId).HasMaxLength(128);
        builder.Property(x => x.ContractId).HasMaxLength(128);
        builder.Property(x => x.VenueContractId).HasMaxLength(128);
        builder.Property(x => x.OrderId).HasMaxLength(128);
    }
}

/// <summary>Phase 2, item 7: venue-forced executions, never fills, links or scored results.</summary>
internal sealed class ForcedEventConfiguration : IEntityTypeConfiguration<ForcedEvent>
{
    public void Configure(EntityTypeBuilder<ForcedEvent> builder)
    {
        builder.ToTable("imported_forced_events", table =>
        {
            table.HasCheckConstraint("CK_imported_forced_events_kind", "\"Kind\" IN ('liquidation', 'deleverage', 'settlement')");
            table.HasCheckConstraint("CK_imported_forced_events_facts",
                "\"Side\" IN ('buy', 'sell') AND \"Quantity\" > 0 AND (\"Price\" IS NULL OR \"Price\" > 0) AND \"FirstSeenAtUtc\" <= \"LastSeenAtUtc\"");
        });
        builder.HasKey(x => x.Id);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.SourceId }).IsUnique();
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.OccurredAtUtc });
        builder.Property(x => x.SourceId).HasMaxLength(128);
        builder.Property(x => x.Kind).HasMaxLength(32);
        builder.Property(x => x.ContractId).HasMaxLength(128);
        builder.Property(x => x.VenueContractId).HasMaxLength(128);
        builder.Property(x => x.Side).HasMaxLength(8);
        builder.Property(x => x.OrderId).HasMaxLength(128);
        builder.Property(x => x.Exclusion).HasMaxLength(200);
    }
}
