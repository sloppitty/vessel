using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.MarketSources;

namespace Vessel.Persistence.Configurations;

internal sealed class DisabledMarketSourceConfiguration : IEntityTypeConfiguration<DisabledMarketSource>
{
    public void Configure(EntityTypeBuilder<DisabledMarketSource> builder)
    {
        builder.ToTable("disabled_market_sources");
        builder.HasKey(x => new { x.OwnerId, x.VenueId, x.SourceId });
        builder.Property(x => x.VenueId).HasMaxLength(DisabledMarketSource.MaxVenueLength);
        builder.Property(x => x.SourceId).HasMaxLength(DisabledMarketSource.MaxSourceLength);
    }
}
