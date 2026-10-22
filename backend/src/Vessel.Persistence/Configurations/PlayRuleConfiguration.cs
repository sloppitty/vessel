using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Plays;

namespace Vessel.Persistence.Configurations;

/// <summary>Phase 2, item 3: versioned if-then rules, one active per owner and trading mode.</summary>
internal sealed class PlayRuleConfiguration : IEntityTypeConfiguration<PlayRule>
{
    public void Configure(EntityTypeBuilder<PlayRule> builder)
    {
        builder.ToTable("play_rules", table =>
        {
            table.HasCheckConstraint("CK_play_rules_version", "\"Version\" >= 1");
            table.HasCheckConstraint("CK_play_rules_trading_mode", "\"TradingMode\" IN ('real', 'paper')");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TradingMode).HasMaxLength(16);
        builder.Property(x => x.If).HasMaxLength(PlayRule.MaxPartLength);
        builder.Property(x => x.Then).HasMaxLength(PlayRule.MaxPartLength);
        builder.HasIndex(x => new { x.OwnerId, x.TradingMode, x.Version }).IsUnique().HasDatabaseName(PlayRuleIndexes.Version);
        builder.HasIndex(x => new { x.OwnerId, x.TradingMode }).IsUnique().HasFilter("\"ArchivedAtUtc\" IS NULL")
            .HasDatabaseName(PlayRuleIndexes.Active);
    }
}

internal static class PlayRuleIndexes
{
    public const string Version = "UX_play_rules_owner_mode_version", Active = "UX_play_rules_owner_mode_active";
}
