using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Accounts;
using Vessel.Domain.Monitoring;

namespace Vessel.Persistence.Configurations;

/// <summary>Phase 2, item 6: the fill coverage ledger, kept beside imported fills and never one of them.</summary>
internal sealed class FillCoverageRecordConfiguration : IEntityTypeConfiguration<FillCoverageRecord>
{
    public void Configure(EntityTypeBuilder<FillCoverageRecord> builder)
    {
        builder.ToTable("account_fill_coverage", table =>
        {
            table.HasCheckConstraint("CK_account_fill_coverage_source", "\"Source\" IN ('refresh', 'check')");
            table.HasCheckConstraint("CK_account_fill_coverage_ranges",
                """
                "FirstObservedAtUtc" <= "LastObservedAtUtc" AND "ImportCount" >= 1 AND
                ("RequestedFromUtc" IS NULL OR "RequestedFromUtc" <= "RequestedToUtc") AND
                ("CoveredFromUtc" IS NULL OR "CoveredToUtc" IS NOT NULL AND "CoveredFromUtc" <= "CoveredToUtc") AND
                ("GapToUtc" IS NULL OR "GapFromUtc" IS NOT NULL AND "GapFromUtc" <= "GapToUtc") AND
                ("Truncated" IS NOT NULL OR "CoveredToUtc" IS NULL AND "GapFromUtc" IS NULL AND NOT "Complete")
                """);
        });
        builder.HasKey(x => x.Id);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.LastObservedAtUtc });
        builder.Property(x => x.Source).HasMaxLength(16);
    }
}

internal sealed class AccountHistoryGapConfiguration : IEntityTypeConfiguration<AccountHistoryGap>
{
    public void Configure(EntityTypeBuilder<AccountHistoryGap> builder)
    {
        builder.ToTable("account_history_gaps", table =>
        {
            table.HasCheckConstraint("CK_account_history_gaps_range",
                """("FromUtc" IS NULL OR "ToUtc" IS NULL OR "FromUtc" <= "ToUtc") AND ("ClosedAtUtc" IS NULL OR "ClosedAtUtc" >= "OpenedAtUtc")""");
        });
        builder.HasKey(x => x.Id);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.OpenedAtUtc });
        builder.Ignore(x => x.IsOpen);
    }
}
