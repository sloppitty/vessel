using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Configurations;

internal sealed class PortfolioConfiguration : IEntityTypeConfiguration<Portfolio>
{
    public void Configure(EntityTypeBuilder<Portfolio> builder)
    {
        builder.ToTable("portfolios", table =>
            table.HasCheckConstraint("CK_portfolios_trading_mode", "\"TradingMode\" IN ('real','paper')"));
        builder.HasKey(x => x.Id);
        builder.HasAlternateKey(x => new { x.OwnerId, x.Id });
        builder.HasAlternateKey(x => new { x.OwnerId, x.Id, x.TradingMode });
        builder.Property(x => x.TradingMode).HasMaxLength(8).HasDefaultValue("real");
        builder.Property(x => x.Name).HasMaxLength(200);
        builder.Property(x => x.Icon).HasMaxLength(PortfolioIcons.MaxLength).HasDefaultValue(PortfolioIcons.Default);
    }
}
