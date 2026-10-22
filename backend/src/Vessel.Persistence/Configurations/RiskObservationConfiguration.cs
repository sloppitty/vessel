using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Configurations;

// Venue-reported liquidation price and margin mode seen by refreshes (Phase 2, item 8a), kept after the snapshot moves on.
internal sealed class RiskObservationConfiguration : IEntityTypeConfiguration<RiskObservation>
{
    public void Configure(EntityTypeBuilder<RiskObservation> builder)
    {
        builder.ToTable("account_risk_observations", table =>
        {
            table.HasCheckConstraint("CK_account_risk_observations_margin_mode", "\"MarginMode\" IS NULL OR \"MarginMode\" IN ('cross', 'isolated')");
            table.HasCheckConstraint("CK_account_risk_observations_reported", "\"LiquidationPrice\" IS NOT NULL OR \"MarginMode\" IS NOT NULL");
            table.HasCheckConstraint("CK_account_risk_observations_liquidation", "\"LiquidationPrice\" IS NULL OR \"LiquidationPrice\" > 0");
        });
        builder.HasKey(x => x.Id);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.ContractId, x.ObservedAtUtc });
        builder.Property(x => x.ContractId).HasMaxLength(128);
        builder.Property(x => x.VenueContractId).HasMaxLength(128);
        builder.Property(x => x.MarginMode).HasMaxLength(16);
    }
}
