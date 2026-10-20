using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", table =>
        {
            table.HasCheckConstraint("CK_accounts_normalized_address", "\"Address\" IS NULL OR \"Address\" = lower(\"Address\")");
            table.HasCheckConstraint("CK_accounts_settings_revision", "\"SettingsRevision\" >= 1");
            table.HasCheckConstraint("CK_accounts_trading_mode", "\"TradingMode\" IN ('real','paper')");
            table.HasCheckConstraint("CK_accounts_paper",
                """("TradingMode"='real' AND "StartingCapitalUsd" IS NULL) OR ("TradingMode"='paper' AND "PortfolioId" IS NOT NULL AND "VenueId"='hyperliquid' AND "Address" IS NULL AND "SourceId" IS NULL AND "ManualAccountValueUsd" IS NULL AND "StartingCapitalUsd" IS NOT NULL AND "StartingCapitalUsd">=0)""");
            table.HasCheckConstraint("CK_accounts_source_identity",
                """("Address" IS NULL OR "SourceId" IS NOT NULL AND "SourceId" = "Address") AND CASE WHEN "SourceId" IS NULL OR "Address" IS NOT NULL THEN true WHEN "SourceId" ~ '^(0|[1-9][0-9]{0,18})$' THEN "SourceId"::numeric <= 9223372036854775807 ELSE false END""");
        });
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.OwnerId, x.Id });
        builder.HasAlternateKey(x => new { x.OwnerId, x.Id, x.TradingMode });
        builder.Property(x => x.TradingMode).HasMaxLength(8).HasDefaultValue("real");
        builder.Property(x => x.StartingCapitalUsd).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(x => x.VenueId).HasMaxLength(64);
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.HasOne<Portfolio>().WithMany().HasForeignKey(x => new { x.OwnerId, x.PortfolioId, x.TradingMode })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id, x.TradingMode }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OwnerId, x.VenueId, x.SourceId }).IsUnique()
            .HasDatabaseName("UX_accounts_owner_venue_source").HasFilter("\"SourceId\" IS NOT NULL");
        builder.Property(x => x.SettingsRevision).HasDefaultValue(1L);
        // With a database default, EF omits a property equal to its sentinel. A true
        // sentinel keeps an explicitly disabled new account from being stored as enabled.
        builder.Property(x => x.IsEnabled).HasDefaultValue(true).HasSentinel(true);
        builder.Property(x => x.Address).HasMaxLength(42);
        builder.Property(x => x.SourceId).HasMaxLength(128);
        builder.Property(x => x.SyncStatus).HasMaxLength(32);
        builder.Property(x => x.LastSyncError).HasMaxLength(200);
        builder.Property(x => x.HistoryNotice).HasMaxLength(1000);
    }
}
