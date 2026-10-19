using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vessel.Domain.Accounts;
using Vessel.Domain.Workspace;

namespace Vessel.Persistence.Configurations;

internal sealed class ImportedFillConfiguration : IEntityTypeConfiguration<ImportedFill>
{
    public void Configure(EntityTypeBuilder<ImportedFill> builder)
    {
        builder.ToTable("imported_fills", table =>
        {
            table.HasCheckConstraint("CK_imported_fills_side", "\"Side\" IN ('buy', 'sell')");
            table.HasCheckConstraint("CK_imported_fills_position_effect", "\"PositionEffect\" IN ('open', 'close', 'flip', 'unknown')");
            table.HasCheckConstraint("CK_imported_fills_fee_basis", "\"FeeBasis\" IN ('reported', 'standard-account-free')");
            table.HasCheckConstraint("CK_imported_fills_pnl_basis", "\"PnlBasis\" IN ('gross', 'net-of-fee')");
            table.HasCheckConstraint("CK_imported_fills_leverage", "\"Leverage\" IS NULL OR \"Leverage\" BETWEEN 1 AND 1000");
        });
        builder.HasKey(x => x.Id);
        builder.HasOne<Account>().WithMany().HasForeignKey(x => new { x.OwnerId, x.AccountId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.ContractId, x.SourceFillId }).IsUnique();
        builder.HasIndex(x => new { x.OwnerId, x.OccurredAtUtc });
        // A fill belongs to the order with the same account, contract and external order ID (LinkedFills, OrderKey).
        builder.HasIndex(x => new { x.OwnerId, x.AccountId, x.ContractId, x.ExternalOrderId });
        builder.Property(x => x.ContractId).HasMaxLength(128);
        builder.Property(x => x.SourceFillId).HasMaxLength(128);
        builder.Property(x => x.VenueContractId).HasMaxLength(128);
        builder.Property(x => x.Side).HasMaxLength(32);
        builder.Property(x => x.Direction).HasMaxLength(128);
        builder.Property(x => x.PositionEffect).HasMaxLength(16);
        builder.Property(x => x.FeeBasis).HasMaxLength(32);
        builder.Property(x => x.PnlBasis).HasMaxLength(16);
        builder.Property(x => x.FeeToken).HasMaxLength(64);
        builder.Property(x => x.ExternalOrderId).HasMaxLength(128);
        builder.Property(x => x.TransactionHash).HasMaxLength(256);
    }
}
